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
    /// POST /collections: a new album. One Odin call: create the album's file,
    /// whose content is the collection as the client sent it (keys and name
    /// already encrypted by the client), plus its new ID.
    /// </summary>
    private static async Task<IResult> Create(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;

        JsonObject stored;
        try
        {
            if (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) is not JsonObject body ||
                body["encryptedKey"] is null || body["type"] is null)
            {
                return EnteResults.Status(400);
            }

            stored = body;
        }
        catch (JsonException)
        {
            return EnteResults.Status(400);
        }

        var id = EnteIds.NewId();
        stored["id"] = id;
        var response = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(EnteIds.ToGuid(EnteIds.Kind.Collection, id), EnteDrive.CollectionFileType,
                stored.ToJsonString()), ct);
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);

        return Results.Json(new
        {
            collection = EnteRecords.Collection(stored, credentials, EnteRecords.NowMicros()),
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
        var stored = EnteRecords.Content(response.Body, credentials);
        if (stored is null) return EnteResults.ApiError(502, "HOMEBASE_ERROR", "The album has no content");

        return Results.Json(new
        {
            collection = EnteRecords.Collection(stored, credentials, OdinClient.UpdatedMicros(response.Body)),
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
            if (EnteRecords.IdOf(header, EnteIds.Kind.Collection) is null) continue;
            if (EnteRecords.Content(header, credentials) is { } stored)
            {
                collections.Add(EnteRecords.Collection(stored, credentials, now));
            }
        }

        return Results.Json(new JsonObject { ["collections"] = collections });
    }

    /// <summary>
    /// GET /collections/v2/diff?collectionID&amp;sinceTime: the album's photos
    /// changed since sinceTime. Two Odin calls (decision 4): the album's
    /// memberships, then the photo records they point to.
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

        var changed = new List<(long FileId, JsonObject Membership, long Updated)>();
        foreach (var header in membershipHeaders)
        {
            var updated = OdinClient.UpdatedMicros(header);
            if (updated <= sinceTime - ClockSkewMicros) continue;
            if (EnteRecords.IdOf(header, EnteIds.Kind.Membership) is not { } ids || ids.Middle != cid) continue;
            if (EnteRecords.Content(header, credentials) is { } membership)
            {
                changed.Add((ids.Id, membership, updated));
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

            var records = new Dictionary<long, (JsonObject Record, long Updated)>();
            foreach (var header in recordHeaders)
            {
                if (EnteRecords.IdOf(header, EnteIds.Kind.File) is { } ids &&
                    EnteRecords.Content(header, credentials) is { } record)
                {
                    records[ids.Id] = (record, OdinClient.UpdatedMicros(header));
                }
            }

            // Oldest first, as Ente's server returns diffs.
            foreach (var (fileId, membership, updated) in changed.OrderBy(c => c.Updated))
            {
                if (!records.TryGetValue(fileId, out var r)) continue;
                diff.Add(EnteRecords.File(r.Record, membership, Math.Max(updated, r.Updated)));
            }
        }

        return Results.Json(new JsonObject { ["diff"] = diff, ["hasMore"] = false });
    }
}
