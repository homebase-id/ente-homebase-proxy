using System.Text.Json.Nodes;
using EnteHomebaseProxy.Auth;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// What the album, file and trash routes do to the library, as Odin calls
/// (records as in <see cref="EnteRecords"/>). Each method says what it costs.
/// A failure comes back as the <see cref="IResult"/> to answer with; null is
/// success. Batches are at most <see cref="MaxBatch"/> photos, Odin's limit.
/// </summary>
public sealed class EnteLibrary(OdinClient odin, HomebaseCredentials credentials, CancellationToken ct)
{
    /// <summary>Most photos one Ente request may name (the web app sends batches of 1000).</summary>
    public const int MaxBatch = 1000;

    private static JsonArray Guids(IEnumerable<Guid> guids) =>
        new([.. guids.Select(g => JsonValue.Create(g.ToString("D")))]);

    private static JsonArray FileGuids(IEnumerable<long> fileIds) =>
        Guids(fileIds.Select(id => EnteIds.ToGuid(EnteIds.Kind.File, id)));

    /// <summary>The photos' headers by Ente ID (active ones; missing ones left out). One call.</summary>
    public async Task<(IResult? Failure, Dictionary<long, JsonNode> Photos)> PhotosAsync(IReadOnlyCollection<long> fileIds)
    {
        var photos = new Dictionary<long, JsonNode>();
        if (fileIds.Count == 0) return (null, photos);
        var (response, headers) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias, new JsonObject
        {
            ["fileType"] = new JsonArray(EnteDrive.FileRecordFileType),
            ["clientUniqueIdAtLeastOne"] = FileGuids(fileIds),
        }, MaxBatch, ct);
        if (!response.IsSuccess) return (EnteResults.OdinFailure(response), photos);
        foreach (var header in headers)
        {
            if (EnteRecords.IdOf(header, EnteIds.Kind.File) is { } ids) photos[ids.Id] = header;
        }

        return (null, photos);
    }

    /// <summary>An album's header, or a 404 result. One call.</summary>
    public async Task<(IResult? Failure, JsonNode? Album)> AlbumAsync(long collectionId)
    {
        var response = await odin.GetHeaderByUniqueIdAsync(credentials, EnteDrive.Alias,
            EnteIds.ToGuid(EnteIds.Kind.Collection, collectionId), ct);
        if (response.Status == 404) return (EnteResults.Status(404), null);
        if (!response.IsSuccess || response.Body is null) return (EnteResults.OdinFailure(response), null);
        return (null, response.Body);
    }

    /// <summary>
    /// Puts photos in an album: one membership create per photo. One already
    /// in the album is left as it is.
    /// </summary>
    public async Task<IResult?> AddAsync(long collectionId, IEnumerable<long> fileIds)
    {
        foreach (var fileId in fileIds)
        {
            var response = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
                EnteRecords.Membership(collectionId, fileId), ct);
            if (!response.IsSuccess && !response.IsExistingUniqueId) return EnteResults.OdinFailure(response);
        }

        return null;
    }

    /// <summary>
    /// Takes photos out of an album: their memberships soft-deleted, so the
    /// album's diff reports each as deleted. Two calls (none if they aren't in it).
    /// </summary>
    public async Task<IResult?> RemoveAsync(long collectionId, IReadOnlyCollection<long> fileIds)
    {
        if (fileIds.Count == 0) return null;
        var (response, memberships) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias, new JsonObject
        {
            ["fileType"] = new JsonArray(EnteDrive.MembershipFileType),
            ["clientUniqueIdAtLeastOne"] =
                Guids(fileIds.Select(id => EnteIds.MembershipGuid(collectionId, id))),
        }, MaxBatch, ct);
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);
        return await SoftDeleteAsync(memberships);
    }

    /// <summary>Every active membership of an album. One call.</summary>
    public async Task<(IResult? Failure, IReadOnlyList<JsonNode> Memberships)> MembershipsOfAlbumAsync(
        long collectionId, bool includeDeleted = false)
    {
        var (response, memberships) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias, new JsonObject
        {
            ["fileType"] = new JsonArray(EnteDrive.MembershipFileType),
            ["groupId"] = Guids([EnteIds.ToGuid(EnteIds.Kind.Collection, collectionId)]),
        }, MaxBatch, ct, includeDeleted);
        return (response.IsSuccess ? null : EnteResults.OdinFailure(response), memberships);
    }

    /// <summary>
    /// Moves photos to the trash, each noting the album it was trashed from:
    /// every membership soft-deleted, a trash entry made, and the photo marked
    /// removed (its bytes and details stay, for a restore). A photo that's
    /// gone or already in the trash is skipped. Four calls, and one per photo.
    /// </summary>
    public async Task<IResult?> TrashAsync(IReadOnlyDictionary<long, long> collectionByFile)
    {
        if (collectionByFile.Count == 0) return null;
        var (failure, photos) = await PhotosAsync([.. collectionByFile.Keys]);
        if (failure is not null) return failure;
        var trashing = photos.Where(p => OdinClient.ArchivalStatus(p.Value) != EnteDrive.Removed)
            .ToDictionary(p => p.Key, p => p.Value);
        if (trashing.Count == 0) return null;

        var (response, memberships) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias, new JsonObject
        {
            ["fileType"] = new JsonArray(EnteDrive.MembershipFileType),
            ["tagsMatchAtLeastOne"] = FileGuids(trashing.Keys),
        }, MaxBatch, ct);
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);
        if (await SoftDeleteAsync(memberships) is { } removeFailure) return removeFailure;

        foreach (var fileId in trashing.Keys)
        {
            var created = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
                EnteRecords.TrashEntry(fileId, collectionByFile[fileId]), ct);
            if (!created.IsSuccess && !created.IsExistingUniqueId) return EnteResults.OdinFailure(created);
        }

        return await SetArchivalStatusAsync(EnteDrive.Removed, trashing.Values);
    }

    /// <summary>The photos' active trash entries by Ente ID, or all of them for null. One call.</summary>
    public async Task<(IResult? Failure, Dictionary<long, JsonNode> Entries)> TrashEntriesAsync(
        IReadOnlyCollection<long>? fileIds)
    {
        var query = new JsonObject { ["fileType"] = new JsonArray(EnteDrive.TrashFileType) };
        if (fileIds is not null)
        {
            if (fileIds.Count == 0) return (null, []);
            query["clientUniqueIdAtLeastOne"] =
                Guids(fileIds.Select(id => EnteIds.ToGuid(EnteIds.Kind.Trash, id)));
        }

        var (response, headers) = await odin.QueryBatchAsync(credentials, EnteDrive.Alias, query, MaxBatch, ct);
        var entries = new Dictionary<long, JsonNode>();
        if (!response.IsSuccess) return (EnteResults.OdinFailure(response), entries);
        foreach (var header in headers)
        {
            if (EnteRecords.IdOf(header, EnteIds.Kind.Trash) is { } ids) entries[ids.Id] = header;
        }

        return (null, entries);
    }

    /// <summary>
    /// Restores photos from the trash into an album: a membership each, the
    /// photo no longer marked removed, and its trash entry closed as restored.
    /// A photo that isn't in the trash is just added. Five calls, and one per photo.
    /// </summary>
    public async Task<IResult?> RestoreAsync(long collectionId, IReadOnlyCollection<long> fileIds)
    {
        var (entriesFailure, entries) = await TrashEntriesAsync(fileIds);
        if (entriesFailure is not null) return entriesFailure;
        var (photosFailure, photos) = await PhotosAsync(fileIds);
        if (photosFailure is not null) return photosFailure;

        var present = fileIds.Where(photos.ContainsKey).ToList();
        if (await AddAsync(collectionId, present) is { } addFailure) return addFailure;
        var removed = present.Select(id => photos[id])
            .Where(p => OdinClient.ArchivalStatus(p) == EnteDrive.Removed).ToList();
        if (await SetArchivalStatusAsync(0, removed) is { } statusFailure) return statusFailure;
        return await CloseTrashEntriesAsync(entries.Values, EnteDrive.TrashRestored);
    }

    /// <summary>
    /// Deletes photos in the trash for good: the photo hard-deleted (bytes
    /// and all), and its trash entry closed as deleted. Photos not in the
    /// trash are left alone, as Ente's server does. Five calls.
    /// </summary>
    public async Task<IResult?> DeleteForeverAsync(IReadOnlyCollection<long>? fileIds)
    {
        var (entriesFailure, entries) = await TrashEntriesAsync(fileIds);
        if (entriesFailure is not null) return entriesFailure;
        if (entries.Count == 0) return null;
        return await DeleteEntriesAsync(entries);
    }

    /// <summary>The trash emptied up to <paramref name="lastUpdatedAtMicros"/> (Ente's /trash/empty). Five calls.</summary>
    public async Task<IResult?> EmptyTrashAsync(long lastUpdatedAtMicros)
    {
        var (failure, entries) = await TrashEntriesAsync(null);
        if (failure is not null) return failure;
        var due = entries.Where(e => OdinClient.UpdatedMicros(e.Value) <= lastUpdatedAtMicros)
            .ToDictionary(e => e.Key, e => e.Value);
        return due.Count == 0 ? null : await DeleteEntriesAsync(due);
    }

    private async Task<IResult?> DeleteEntriesAsync(Dictionary<long, JsonNode> entries)
    {
        var (photosFailure, photos) = await PhotosAsync([.. entries.Keys]);
        if (photosFailure is not null) return photosFailure;
        var trashed = photos.Values.Where(p => OdinClient.ArchivalStatus(p) == EnteDrive.Removed).ToList();
        if (trashed.Count > 0)
        {
            var deleted = await odin.HardDeleteAsync(credentials, EnteDrive.Alias, trashed, ct);
            if (BatchFailure(deleted) is { } deleteFailure) return deleteFailure;
        }

        return await CloseTrashEntriesAsync(entries.Values, EnteDrive.TrashDeleted);
    }

    /// <summary>Trash entries marked restored or deleted, then soft-deleted. Up to two calls.</summary>
    private async Task<IResult?> CloseTrashEntriesAsync(IEnumerable<JsonNode> entries, int outcome)
    {
        var list = entries.ToList();
        if (list.Count == 0) return null;
        var marked = await odin.SetArchivalStatusAsync(credentials, EnteDrive.Alias, outcome, list, ct);
        if (BatchFailure(marked) is { } markFailure) return markFailure;
        return await SoftDeleteAsync(list);
    }

    /// <summary>Files soft-deleted (Odin's tombstones). One call, none for an empty list.</summary>
    public async Task<IResult?> SoftDeleteAsync(IEnumerable<JsonNode> headers)
    {
        var ids = headers.Select(OdinClient.FileId).OfType<Guid>().ToList();
        if (ids.Count == 0) return null;
        return BatchFailure(await odin.SoftDeleteAsync(credentials, EnteDrive.Alias, ids, ct));
    }

    /// <summary>Only appData.archivalStatus set on each file. One call, none for an empty list.</summary>
    public async Task<IResult?> SetArchivalStatusAsync(int status, IEnumerable<JsonNode> headers)
    {
        var list = headers.ToList();
        if (list.Count == 0) return null;
        return BatchFailure(await odin.SetArchivalStatusAsync(credentials, EnteDrive.Alias, status, list, ct));
    }

    /// <summary>A batch call's failure as the result to answer with: the call itself, or any one file in it.</summary>
    private static IResult? BatchFailure(OdinResponse response)
    {
        if (!response.IsSuccess) return EnteResults.OdinFailure(response);
        var failed = OdinClient.BatchFailures(response);
        return failed.Count == 0
            ? null
            : EnteResults.ApiError(502, "HOMEBASE_ERROR", $"Homebase didn't change {failed.Count} file(s): {failed[0]}");
    }
}
