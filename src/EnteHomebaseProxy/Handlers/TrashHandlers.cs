using System.Text.Json.Nodes;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's trash: /trash routes, trashing files and collections, and restoring
/// files (server/cmd/museum/main.go). A photo in the trash keeps its bytes
/// and details, marked removed, with a trash entry beside it (see
/// <see cref="EnteDrive.TrashFileType"/> and <see cref="EnteLibrary"/>).
/// </summary>
public static class TrashHandlers
{
    /// <summary>How far before sinceTime the trash diff looks again (see CollectionsHandlers).</summary>
    private static readonly long ClockSkewMicros = (long)TimeSpan.FromMinutes(10).TotalMicroseconds;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/files/trash", TrashFiles);
        app.MapDelete("/collections/v3/{collectionID:long}", DeleteCollection);
        app.MapPost("/collections/restore-files", RestoreFiles);
        app.MapGet("/trash/v2/diff", GetDiff);
        app.MapPost("/trash/delete", DeleteForever);
        app.MapPost("/trash/empty", Empty);
    }

    /// <summary>
    /// POST /files/trash, body {items: [{fileID, collectionID}]}: photos to the
    /// trash, out of every album (see <see cref="EnteLibrary.TrashAsync"/>).
    /// </summary>
    private static async Task<IResult> TrashFiles(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            body["items"] is not JsonArray items || items.Count is 0 or > EnteLibrary.MaxBatch)
        {
            return EnteResults.Status(400);
        }

        var collectionByFile = new Dictionary<long, long>();
        foreach (var item in items)
        {
            if (EnteRecords.Id(item?["fileID"]) is not { } fileId ||
                EnteRecords.Id(item?["collectionID"]) is not { } collectionId)
            {
                return EnteResults.Status(400);
            }

            collectionByFile.TryAdd(fileId, collectionId);
        }

        return await new EnteLibrary(odin, credentials, ct).TrashAsync(collectionByFile) ?? Results.Ok();
    }

    /// <summary>
    /// DELETE /collections/v3/{collectionID}?keepFiles: deletes an album. With
    /// keepFiles it must be empty (the web app moves its photos out first;
    /// 409 COLLECTION_NOT_EMPTY otherwise, as Ente's server). Without, its
    /// photos go to the trash, out of every other album too (Ente's "delete
    /// photos"). The album's file stays, marked removed, so the album list
    /// reports it deleted.
    /// </summary>
    private static async Task<IResult> DeleteCollection(long collectionID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (collectionID is <= 0 or > EnteIds.MaxSafeInteger ||
            !bool.TryParse(context.Request.Query["keepFiles"].ToString(), out var keepFiles))
        {
            return EnteResults.Status(400);
        }

        var library = new EnteLibrary(odin, credentials, ct);
        var (albumFailure, album) = await library.AlbumAsync(collectionID);
        if (albumFailure is not null) return albumFailure;
        if (OdinClient.ArchivalStatus(album!) == EnteDrive.Removed) return Results.Ok();

        var (membershipsFailure, memberships) = await library.MembershipsOfAlbumAsync(collectionID);
        if (membershipsFailure is not null) return membershipsFailure;
        var fileIds = memberships
            .Select(m => EnteRecords.IdOf(m, EnteIds.Kind.Membership))
            .OfType<(long Middle, long Id)>()
            .Where(ids => ids.Middle == collectionID)
            .Select(ids => ids.Id)
            .ToList();
        if (fileIds.Count > 0)
        {
            if (keepFiles)
            {
                return EnteResults.ApiError(409, "COLLECTION_NOT_EMPTY", "The collection is not empty");
            }

            if (await library.TrashAsync(fileIds.ToDictionary(id => id, _ => collectionID)) is { } trashFailure)
            {
                return trashFailure;
            }
        }

        return await library.SetArchivalStatusAsync(EnteDrive.Removed, [album!]) ?? Results.Ok();
    }

    /// <summary>
    /// POST /collections/restore-files, body {collectionID, files: [{id, …}]}:
    /// photos out of the trash into an album (see <see cref="EnteLibrary.RestoreAsync"/>).
    /// </summary>
    private static async Task<IResult> RestoreFiles(HttpContext context, OdinClient odin, CancellationToken ct)
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
        return albumFailure ?? await library.RestoreAsync(collectionId, fileIds) ?? Results.Ok();
    }

    /// <summary>
    /// GET /trash/v2/diff?sinceTime: the trash's changes. Two calls: the trash
    /// entries, tombstones included, then the photos still in it. A closed
    /// entry is reported restored or deleted, by the outcome it was marked with.
    /// </summary>
    private static async Task<IResult> GetDiff(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        var since = (EnteRecords.QueryLong(context, "sinceTime") ?? 0) - ClockSkewMicros;

        var (response, entries) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias,
            new JsonObject { ["fileType"] = new JsonArray(EnteDrive.TrashFileType) },
            EnteLibrary.MaxBatch, ct, includeDeleted: true);
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);

        // Per photo, its latest entry: open, or one closed.
        var latest = new Dictionary<long, JsonNode>();
        foreach (var entry in entries)
        {
            if (OdinClient.UpdatedMicros(entry) <= since ||
                EnteRecords.TaggedId(entry, EnteIds.Kind.File) is not { } fileId)
            {
                continue;
            }

            if (!latest.TryGetValue(fileId, out var seen) ||
                (!OdinClient.IsDeleted(entry) && OdinClient.IsDeleted(seen)) ||
                (OdinClient.IsDeleted(entry) == OdinClient.IsDeleted(seen) &&
                 OdinClient.UpdatedMicros(entry) > OdinClient.UpdatedMicros(seen)))
            {
                latest[fileId] = entry;
            }
        }

        var library = new EnteLibrary(odin, credentials, ct);
        var (photosFailure, photos) = await library.PhotosAsync(
            [.. latest.Where(l => !OdinClient.IsDeleted(l.Value)).Select(l => l.Key)]);
        if (photosFailure is not null) return photosFailure;

        var diff = new List<(long Updated, JsonObject Item)>();
        foreach (var (fileId, entry) in latest)
        {
            var updated = OdinClient.UpdatedMicros(entry);
            var collectionId = EnteRecords.GroupCollectionId(entry) ?? 0;
            var closed = OdinClient.IsDeleted(entry);
            JsonObject? file = null;
            if (!closed && photos.TryGetValue(fileId, out var photo))
            {
                file = EnteRecords.File(fileId, collectionId, photo, credentials, updated);
            }

            // An open entry whose photo is gone counts as deleted.
            var deleted = closed ? OdinClient.ArchivalStatus(entry) == EnteDrive.TrashDeleted : file is null;
            var restored = closed && !deleted;
            file ??= EnteRecords.DeletedFile(fileId, collectionId, credentials, updated);
            var created = OdinClient.CreatedMicros(entry);
            diff.Add((updated, new JsonObject
            {
                ["file"] = file,
                ["isDeleted"] = deleted,
                ["isRestored"] = restored,
                ["createdAt"] = created,
                ["updatedAt"] = updated,
                ["deleteBy"] = created + EnteDrive.TrashMicros,
            }));
        }

        return Results.Json(new JsonObject
        {
            ["diff"] = new JsonArray([.. diff.OrderBy(d => d.Updated).Select(d => (JsonNode)d.Item)]),
            ["hasMore"] = false,
        });
    }

    /// <summary>POST /trash/delete, body {fileIDs}: photos in the trash deleted for good.</summary>
    private static async Task<IResult> DeleteForever(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Ids(body["fileIDs"], EnteLibrary.MaxBatch) is not { } fileIds)
        {
            return EnteResults.Status(400);
        }

        return await new EnteLibrary(odin, credentials, ct).DeleteForeverAsync(fileIds) ?? Results.Ok();
    }

    /// <summary>POST /trash/empty, body {lastUpdatedAt}: the trash, as the client last saw it, deleted for good.</summary>
    private static async Task<IResult> Empty(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (await EnteRecords.BodyAsync(context, ct) is not { } body ||
            EnteRecords.Long(body["lastUpdatedAt"]) is not { } lastUpdatedAt || lastUpdatedAt < 0)
        {
            return EnteResults.Status(400);
        }

        return await new EnteLibrary(odin, credentials, ct).EmptyTrashAsync(lastUpdatedAt) ?? Results.Ok();
    }
}
