namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's trash: /trash routes, trashing files and collections, and restoring
/// files (server/cmd/museum/main.go). All TODOs for now.
/// </summary>
public static class TrashHandlers
{
    public static void Map(IEndpointRouteBuilder app)
    {
        Todo.Map(app, "POST", "/files/trash",
            "Trashes many files. Would be POST /api/v2/drives/{alias}/files/soft-delete-batch (one call), but " +
            "Ente can restore from trash, and an Odin soft delete leaves a tombstone without the payloads or " +
            "content (DriveStorageServiceBase.WriteDeletedFileHeader), so it would need to be a " +
            "PATCH marking each file trashed instead (one call per file). " + Why.IntIds);
        Todo.Map(app, "DELETE", "/collections/v3/{collectionID}",
            "Trashes a collection, and optionally its files: one call per file. " + Why.IntIds);
        Todo.Map(app, "POST", "/collections/restore-files",
            "Restores trashed files into a collection: one PATCH per file. " + Why.IntIds);
        Todo.Map(app, "GET", "/trash/v2/diff",
            "Would be POST /api/v2/drives/{alias}/files/query-batch for trashed files. " + Why.Diff);
        Todo.Map(app, "POST", "/trash/delete",
            "Would be POST /api/v2/drives/{alias}/files/hard-delete-batch (one call for many files), but it " +
            "takes Odin file IDs and versionTags. " + Why.IntIds);
        Todo.Map(app, "POST", "/trash/empty",
            "Deletes everything in the trash up to a time: needs a query for the trashed files, then a " +
            "hard-delete-batch: two calls.");
    }
}
