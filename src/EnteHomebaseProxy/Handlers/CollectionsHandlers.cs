namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's /collections routes (server/cmd/museum/main.go), except sharing
/// (see <see cref="SharingHandlers"/>) and trash (see <see cref="TrashHandlers"/>).
/// All TODOs for now.
/// </summary>
public static class CollectionsHandlers
{
    public static void Map(IEndpointRouteBuilder app)
    {
        Todo.Map(app, "POST", "/collections",
            "Would be POST /api/v2/drives/{alias}/files: one Odin file per collection, holding Ente's " +
            "encrypted collection record (already encrypted by the client) as content. Ente expects an int64 " +
            "collection ID back. " + Why.IntIds);
        Todo.Map(app, "GET", "/collections/{collectionID}",
            "Would be GET /api/v2/drives/{alias}/files/{fileId}/header. " + Why.IntIds);
        Todo.Map(app, "GET", "/collections",
            "Deprecated by /collections/v2. Would be POST /api/v2/drives/{alias}/files/query-batch for the " +
            "collection fileType. " + Why.IntIds);
        Todo.Map(app, "GET", "/collections/v2", Why.Diff);
        Todo.Map(app, "GET", "/collections/v3", Why.Diff);
        Todo.Map(app, "GET", "/collections/v2/diff", Why.Diff);
        Todo.Map(app, "GET", "/collections/file",
            "Reads one file within a collection. Would be GET /api/v2/drives/{alias}/files/{fileId}/header. " +
            Why.IntIds);
        Todo.Map(app, "POST", "/collections/add-files",
            "Adds many files to a collection. On Homebase a file's collections would be its tags or groupId, " +
            "so this is one PATCH per file: not one call.");
        Todo.Map(app, "POST", "/collections/move-files", "One PATCH per file (see add-files): not one call.");
        Todo.Map(app, "POST", "/collections/v3/remove-files", "One PATCH per file (see add-files): not one call.");
        Todo.Map(app, "POST", "/collections/suggest-delete", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/rename",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId} with the new encrypted name, but needs the " +
            "current versionTag (a read first). " + Why.IntIds);
        Todo.Map(app, "PUT", "/collections/magic-metadata",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId}, but Ente's version check needs the current " +
            "versionTag (a read first). " + Why.IntIds);
        Todo.Map(app, "PUT", "/collections/public-magic-metadata",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId}, but Ente's version check needs the current " +
            "versionTag (a read first). " + Why.IntIds);
    }
}
