namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's /files routes (server/cmd/museum/main.go), except sharing (see
/// <see cref="SharingHandlers"/>) and trash (see <see cref="TrashHandlers"/>).
/// All TODOs for now.
/// </summary>
public static class FilesHandlers
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Object bytes: Ente's clients talk to S3 directly.
        Todo.Map(app, "GET", "/files/upload-eligibility",
            "Checks storage quota before an upload. Odin enforces quota on the upload itself (507); there's " +
            "no read-only quota check to call.");
        Todo.Map(app, "GET", "/files/upload-urls", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/multipart-upload-urls", Why.PresignedUrls);
        Todo.Map(app, "POST", "/files/upload-url", Why.PresignedUrls);
        Todo.Map(app, "POST", "/files/multipart-upload-url", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/download/{fileID}",
            "Would be GET /api/v2/drives/{alias}/files/{fileId}/payload/{key} (the encrypted bytes, passed " +
            "through). " + Why.IntIds);
        Todo.Map(app, "GET", "/files/download/v2/{fileID}", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/download/v3/{fileID}", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/preview/{fileID}",
            "Would be GET /api/v2/drives/{alias}/files/{fileId}/payload/{key} for the thumbnail payload. " +
            Why.IntIds);
        Todo.Map(app, "GET", "/files/preview/v2/{fileID}", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/thumbnail/v3/{fileID}", Why.PresignedUrls);

        // Derived file data.
        Todo.Map(app, "PUT", "/files/data", Why.FileData);
        Todo.Map(app, "PUT", "/files/video-data", Why.FileData);
        Todo.Map(app, "POST", "/files/data/status-diff", Why.FileData);
        Todo.Map(app, "POST", "/files/data/fetch", Why.FileData);
        Todo.Map(app, "GET", "/files/data/fetch", Why.FileData);
        Todo.Map(app, "GET", "/files/data/preview-upload-url", Why.PresignedUrls);
        Todo.Map(app, "POST", "/files/data/preview-upload-url", Why.PresignedUrls);
        Todo.Map(app, "POST", "/files/data/multipart-preview-upload-url", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/data/preview", Why.PresignedUrls);

        // File records.
        Todo.Map(app, "POST", "/files",
            "Would be POST /api/v2/drives/{alias}/files: one Odin file holding Ente's encrypted file record " +
            "(keys and metadata, already encrypted by the client) as content, with the object bytes as " +
            "payloads. But the bytes were uploaded to presigned URLs first (see upload-urls), and Ente " +
            "expects an int64 ID back. " + Why.IntIds);
        Todo.Map(app, "POST", "/files/meta",
            "Would be POST /api/v2/drives/{alias}/files with the encrypted record as content. " + Why.IntIds);
        Todo.Map(app, "POST", "/files/copy",
            "Copies files into another collection: one Odin create per file, so not one call.");
        Todo.Map(app, "PUT", "/files/update",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId} with the new record. " + Why.IntIds);
        Todo.Map(app, "POST", "/files/size",
            "Sums the sizes of many files: one header read per file, or a query-batch plus the ID mapping. " +
            Why.IntIds);
        Todo.Map(app, "POST", "/files/info", "Reads many files' details. " + Why.IntIds);
        Todo.Map(app, "GET", "/files/duplicates",
            "Finds files with the same hash across the library: needs a scan over every file (many calls).");
        Todo.Map(app, "PUT", "/files/thumbnail",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId} replacing the thumbnail payload. " + Why.IntIds);
        Todo.Map(app, "PUT", "/files/magic-metadata",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId}, but Ente's version check needs the current " +
            "versionTag (a read first). " + Why.IntIds);
        Todo.Map(app, "PUT", "/files/public-magic-metadata",
            "Would be PATCH /api/v2/drives/{alias}/files/{fileId}, but Ente's version check needs the current " +
            "versionTag (a read first). " + Why.IntIds);
        Todo.Map(app, "GET", "/files/count",
            "Public count of all files ever stored on Ente (marketing); there's nothing to ask Homebase.");
    }
}
