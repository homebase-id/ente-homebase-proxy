using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Auth;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's /collections routes (server/cmd/museum/main.go), except sharing
/// (see <see cref="SharingHandlers"/>) and trash (see <see cref="TrashHandlers"/>).
/// Albums and their photos are kept as in <see cref="EnteRecords"/>.
/// </summary>
public static class CollectionsHandlers
{
    /// <summary>Most albums or memberships one call returns (no paging yet).</summary>
    private const int MaxRecords = 1000;

    /// <summary>
    /// How far before sinceTime a diff looks again. The client may store the
    /// proxy's clock as its sinceTime (the album's updationTime), while changes
    /// carry Odin's clock; repeats are harmless (the client keys by ID).
    /// </summary>
    private static readonly long ClockSkewMicros = (long)TimeSpan.FromMinutes(10).TotalMicroseconds;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/collections", Create);
        app.MapGet("/collections/{collectionID:long}", GetById);
        app.MapGet("/collections/v2", GetV2);
        app.MapGet("/collections/v2/diff", GetDiffV2);
        app.MapPost("/collections/add-files", AddFiles);
        app.MapPost("/collections/v3/remove-files", RemoveFiles);
        app.MapPost("/collections/move-files", MoveFiles);
        app.MapPost("/collections/rename", Rename);
        app.MapPut("/collections/magic-metadata", UpdateSealed);
        app.MapPut("/collections/public-magic-metadata", UpdateSealed);

        Todo.Map(app, "GET", "/collections",
            "Deprecated by /collections/v2, which is implemented.");
        Todo.Map(app, "GET", "/collections/v3",
            "Paged /collections/v2 (with a limit). Would be the same query-batch with a cursor; the web " +
            "client uses v2.");
        Todo.Map(app, "GET", "/collections/file",
            "Reads one file within a collection. Would be GET /api/v2/drives/{alias}/files/by-uid/{uid}/header " +
            "for the membership and the record: two calls.");
        Todo.Map(app, "POST", "/collections/suggest-delete", Why.OtherUsers);
    }

    /// <summary>
    /// POST /collections, body {"homebase": sealed record}: a new album. One
    /// Odin call, which creates the album's file as the web app sealed it,
    /// under a new ID.
    /// </summary>
    private static async Task<IResult> Create(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;

        SealedRecord sealedRecord;
        try
        {
            if (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) is not JsonObject body ||
                SealedRecord.Read(body["homebase"]) is not { } read)
            {
                return EnteResults.Status(400);
            }

            sealedRecord = read;
        }
        catch (JsonException)
        {
            return EnteResults.Status(400);
        }

        var id = EnteIds.NewId();
        var response = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(EnteIds.ToGuid(EnteIds.Kind.Collection, id), EnteDrive.CollectionFileType, sealedRecord),
            ct);
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);

        return Results.Json(new
        {
            collection = EnteRecords.Collection(id, sealedRecord, credentials, EnteRecords.NowMicros()),
        });
    }

    /// <summary>
    /// POST /collections/add-files, body {collectionID, files: [{id,
    /// encryptedKey, keyDecryptionNonce}]}: puts photos the owner already has
    /// in an album. The encrypted keys are Ente's and mean nothing here (a
    /// photo has one key, its own), so only the IDs are read. One Odin call for
    /// the album, then one membership create per photo (decision 5). A photo
    /// already in the album is left as it is.
    /// </summary>
    private static async Task<IResult> AddFiles(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Id(body["collectionID"]) is not { } collectionId ||
            EnteRecords.Ids(body["files"], EnteLibrary.MaxBatch) is not { } fileIds)
        {
            return EnteResults.Status(400);
        }

        var library = new EnteLibrary(odin, credentials, ct);
        var (albumFailure, _) = await library.AlbumAsync(collectionId);
        return albumFailure ?? await library.AddAsync(collectionId, fileIds) ?? Results.Ok();
    }

    /// <summary>
    /// POST /collections/v3/remove-files, body {collectionID, fileIDs}: takes
    /// photos out of an album. The web app moves a photo that's in no other
    /// album to Uncategorized first, as Ente has no orphans. Two calls: their
    /// memberships, then a soft delete of them.
    /// </summary>
    private static async Task<IResult> RemoveFiles(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Id(body["collectionID"]) is not { } collectionId ||
            EnteRecords.Ids(body["fileIDs"], EnteLibrary.MaxBatch) is not { } fileIds)
        {
            return EnteResults.Status(400);
        }

        return await new EnteLibrary(odin, credentials, ct).RemoveAsync(collectionId, fileIds) ?? Results.Ok();
    }

    /// <summary>
    /// POST /collections/move-files, body {fromCollectionID, toCollectionID,
    /// files: [{id, …}]}: add-files to one album, then remove-files from the
    /// other. One call for the album, one per photo, and two.
    /// </summary>
    private static async Task<IResult> MoveFiles(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Id(body["fromCollectionID"]) is not { } from ||
            EnteRecords.Id(body["toCollectionID"]) is not { } to || from == to ||
            EnteRecords.Ids(body["files"], EnteLibrary.MaxBatch) is not { } fileIds)
        {
            return EnteResults.Status(400);
        }

        var library = new EnteLibrary(odin, credentials, ct);
        var (albumFailure, _) = await library.AlbumAsync(to);
        return albumFailure ?? await library.AddAsync(to, fileIds) ??
            await library.RemoveAsync(from, fileIds) ?? Results.Ok();
    }

    /// <summary>
    /// POST /collections/rename, body {collectionID, homebase}: the album's
    /// details sealed again by the web app with the new name (Ente's
    /// encryptedName has no meaning here). Two calls: see <see cref="Reseal"/>.
    /// </summary>
    private static async Task<IResult> Rename(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Id(body["collectionID"]) is not { } collectionId ||
            SealedRecord.Read(body["homebase"]) is not { } sealedRecord)
        {
            return EnteResults.Status(400);
        }

        return await Reseal(odin, credentials, collectionId, sealedRecord, ct);
    }

    /// <summary>
    /// PUT /collections/magic-metadata and /collections/public-magic-metadata,
    /// body {id, homebase}: the album's details sealed again by the web app
    /// with the new settings. Two calls: see <see cref="Reseal"/>.
    /// </summary>
    private static async Task<IResult> UpdateSealed(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Id(body["id"]) is not { } collectionId ||
            SealedRecord.Read(body["homebase"]) is not { } sealedRecord)
        {
            return EnteResults.Status(400);
        }

        return await Reseal(odin, credentials, collectionId, sealedRecord, ct);
    }

    /// <summary>The album's file updated with a new sealed record: its header, then the update.</summary>
    private static async Task<IResult> Reseal(OdinClient odin, HomebaseCredentials credentials, long collectionId,
        SealedRecord sealedRecord, CancellationToken ct)
    {
        var (albumFailure, album) = await new EnteLibrary(odin, credentials, ct).AlbumAsync(collectionId);
        if (albumFailure is not null) return albumFailure;
        var response = await odin.UpdateSealedAsync(credentials, EnteDrive.Alias,
            EnteIds.ToGuid(EnteIds.Kind.Collection, collectionId), album!, sealedRecord, ct);
        return response.IsSuccess ? Results.Ok() : EnteResults.OdinFailure(response);
    }

    /// <summary>GET /collections/{id}: one album. One Odin call: its header by uniqueId.</summary>
    private static async Task<IResult> GetById(long collectionID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (collectionID is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(404);

        var response = await odin.GetHeaderByUniqueIdAsync(credentials, EnteDrive.Alias,
            EnteIds.ToGuid(EnteIds.Kind.Collection, collectionID), ct);
        if (response.Status == 404) return EnteResults.Status(404);
        if (!response.IsSuccess || response.Body is null) return EnteResults.OdinFailure(response);
        if (SealedRecord.FromHeader(response.Body) is not { } sealedRecord)
        {
            return EnteResults.ApiError(502, "HOMEBASE_ERROR", "The album isn't sealed by the web app");
        }

        return Results.Json(new
        {
            collection = EnteRecords.Collection(collectionID, sealedRecord, credentials,
                OdinClient.UpdatedMicros(response.Body),
                isDeleted: OdinClient.ArchivalStatus(response.Body) == EnteDrive.Removed),
        });
    }

    /// <summary>
    /// GET /collections/v2?sinceTime: every album. One Odin call: query the
    /// album files.
    ///
    /// Each album's updationTime is now. Ente bumps an album when its photos
    /// change, and the client only asks an album for changes when that time
    /// moves; doing that here would cost a third call per upload. So every
    /// pull checks every album's diff instead (cheap: it returns only changes).
    /// </summary>
    private static async Task<IResult> GetV2(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;

        var (response, headers) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias,
            new JsonObject { ["fileType"] = new JsonArray(EnteDrive.CollectionFileType) }, MaxRecords, ct);
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);

        var now = EnteRecords.NowMicros();
        var collections = new JsonArray();
        foreach (var header in headers)
        {
            if (EnteRecords.IdOf(header, EnteIds.Kind.Collection) is { } ids &&
                SealedRecord.FromHeader(header) is { } sealedRecord)
            {
                collections.Add(EnteRecords.Collection(ids.Id, sealedRecord, credentials, now,
                    isDeleted: OdinClient.ArchivalStatus(header) == EnteDrive.Removed));
            }
        }

        return Results.Json(new JsonObject { ["collections"] = collections });
    }

    /// <summary>
    /// GET /collections/v2/diff?collectionID&amp;sinceTime: the album's photos
    /// changed since sinceTime. Two Odin calls (decision 4): the album's
    /// memberships, tombstones included, then the photos they point to.
    ///
    /// A photo is in the diff when its membership changed or the photo itself
    /// did (an edit to its details). One whose membership is a tombstone
    /// (removed, moved, trashed), or whose photo is gone or in the trash, is
    /// reported deleted.
    /// </summary>
    private static async Task<IResult> GetDiffV2(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        var collectionID = EnteRecords.QueryLong(context, "collectionID");
        var sinceTime = EnteRecords.QueryLong(context, "sinceTime") ?? 0;
        if (collectionID is not { } cid || cid is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(400);

        var library = new EnteLibrary(odin, credentials, ct);
        var (membershipsFailure, memberships) = await library.MembershipsOfAlbumAsync(cid, includeDeleted: true);
        if (membershipsFailure is not null) return membershipsFailure;

        // Per photo, its latest membership: active, or the tombstone of one.
        var latest = new Dictionary<long, (bool Active, long Updated)>();
        foreach (var header in memberships)
        {
            var active = !OdinClient.IsDeleted(header);
            long? fileId = active
                ? EnteRecords.IdOf(header, EnteIds.Kind.Membership) is { } ids && ids.Middle == cid ? ids.Id : null
                : EnteRecords.GroupCollectionId(header) == cid ? EnteRecords.TaggedId(header, EnteIds.Kind.File) : null;
            if (fileId is not { } id) continue;
            var updated = OdinClient.UpdatedMicros(header);
            if (!latest.TryGetValue(id, out var seen) || (active && !seen.Active) ||
                (active == seen.Active && updated > seen.Updated))
            {
                latest[id] = (active, updated);
            }
        }

        var (photosFailure, photos) = await library.PhotosAsync([.. latest.Where(l => l.Value.Active).Select(l => l.Key)]);
        if (photosFailure is not null) return photosFailure;

        var since = sinceTime - ClockSkewMicros;
        var changes = new List<(long Updated, JsonObject File)>();
        foreach (var (fileId, (active, membershipUpdated)) in latest)
        {
            JsonObject? file = null;
            var updated = membershipUpdated;
            if (active && photos.TryGetValue(fileId, out var photo) &&
                OdinClient.ArchivalStatus(photo) != EnteDrive.Removed)
            {
                updated = Math.Max(membershipUpdated, OdinClient.UpdatedMicros(photo));
                if (updated > since) file = EnteRecords.File(fileId, cid, photo, credentials, updated);
            }
            else if (updated > since)
            {
                file = EnteRecords.DeletedFile(fileId, cid, credentials, updated);
            }

            if (file is not null) changes.Add((updated, file));
        }

        // Oldest first, as Ente's server returns diffs.
        var diff = new JsonArray([.. changes.OrderBy(c => c.Updated).Select(c => (JsonNode)c.File)]);
        return Results.Json(new JsonObject { ["diff"] = diff, ["hasMore"] = false });
    }
}
