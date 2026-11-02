using System.Text.Json;
using System.Text.Json.Nodes;
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

        Todo.Map(app, "GET", "/collections",
            "Deprecated by /collections/v2, which is implemented.");
        Todo.Map(app, "GET", "/collections/v3",
            "Paged /collections/v2 (with a limit). Would be the same query-batch with a cursor; the web " +
            "client uses v2.");
        Todo.Map(app, "GET", "/collections/file",
            "Reads one file within a collection. Would be GET /api/v2/drives/{alias}/files/by-uid/{uid}/header " +
            "for the membership and the record: two calls.");
        Todo.Map(app, "POST", "/collections/add-files",
            "Adds photos to an album: one membership create per photo (allowed by decision 5).");
        Todo.Map(app, "POST", "/collections/move-files",
            "Moves photos between albums: per photo, create the new membership and soft-delete the old one.");
        Todo.Map(app, "POST", "/collections/v3/remove-files",
            "Removes photos from an album: one membership soft-delete per photo (the tombstone keeps the " +
            "album, so the diff can report the removal).");
        Todo.Map(app, "POST", "/collections/suggest-delete", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/rename",
            "Would be PATCH /api/v2/drives/{alias}/files/by-uid/{uid} of the album with the new encrypted " +
            "name; needs the current versionTag (a read first): two calls.");
        Todo.Map(app, "PUT", "/collections/magic-metadata",
            "Would be PATCH of the album by uid; Ente's version check needs the current versionTag (a read " +
            "first): two calls.");
        Todo.Map(app, "PUT", "/collections/public-magic-metadata",
            "Would be PATCH of the album by uid; Ente's version check needs the current versionTag (a read " +
            "first): two calls.");
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
                OdinClient.UpdatedMicros(response.Body)),
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
                collections.Add(EnteRecords.Collection(ids.Id, sealedRecord, credentials, now));
            }
        }

        return Results.Json(new JsonObject { ["collections"] = collections });
    }

    /// <summary>
    /// GET /collections/v2/diff?collectionID&amp;sinceTime: the album's photos
    /// changed since sinceTime. Two Odin calls (decision 4): the album's
    /// memberships, then the photos they point to.
    /// </summary>
    private static async Task<IResult> GetDiffV2(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        var collectionID = EnteRecords.QueryLong(context, "collectionID");
        var sinceTime = EnteRecords.QueryLong(context, "sinceTime") ?? 0;
        if (collectionID is not { } cid || cid is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(400);

        var (membershipsResponse, membershipHeaders) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias,
            new JsonObject
            {
                ["fileType"] = new JsonArray(EnteDrive.MembershipFileType),
                ["groupId"] = new JsonArray(EnteIds.ToGuid(EnteIds.Kind.Collection, cid).ToString("D")),
            }, MaxRecords, ct);
        if (!membershipsResponse.IsSuccess) return EnteResults.OdinFailure(membershipsResponse);

        // A membership holds nothing but its uniqueId (album and photo).
        var changed = new List<(long FileId, long Updated)>();
        foreach (var header in membershipHeaders)
        {
            var updated = OdinClient.UpdatedMicros(header);
            if (updated <= sinceTime - ClockSkewMicros) continue;
            if (EnteRecords.IdOf(header, EnteIds.Kind.Membership) is { } ids && ids.Middle == cid)
            {
                changed.Add((ids.Id, updated));
            }
        }

        var diff = new JsonArray();
        if (changed.Count > 0)
        {
            var (recordsResponse, recordHeaders) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias,
                new JsonObject
                {
                    ["fileType"] = new JsonArray(EnteDrive.FileRecordFileType),
                    ["clientUniqueIdAtLeastOne"] = new JsonArray([
                        .. changed.Select(c =>
                            JsonValue.Create(EnteIds.ToGuid(EnteIds.Kind.File, c.FileId).ToString("D"))),
                    ]),
                }, MaxRecords, ct);
            if (!recordsResponse.IsSuccess) return EnteResults.OdinFailure(recordsResponse);

            var photos = new Dictionary<long, JsonNode>();
            foreach (var header in recordHeaders)
            {
                if (EnteRecords.IdOf(header, EnteIds.Kind.File) is { } ids) photos[ids.Id] = header;
            }

            // Oldest first, as Ente's server returns diffs.
            foreach (var (fileId, updated) in changed.OrderBy(c => c.Updated))
            {
                if (!photos.TryGetValue(fileId, out var header)) continue;
                var file = EnteRecords.File(fileId, cid, header, credentials,
                    Math.Max(updated, OdinClient.UpdatedMicros(header)));
                if (file is not null) diff.Add(file);
            }
        }

        return Results.Json(new JsonObject { ["diff"] = diff, ["hasMore"] = false });
    }
}
