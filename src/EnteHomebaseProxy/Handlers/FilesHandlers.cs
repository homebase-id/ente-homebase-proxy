using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Odin;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's /files routes (server/cmd/museum/main.go), except sharing (see
/// <see cref="SharingHandlers"/>) and trash (see <see cref="TrashHandlers"/>).
///
/// A photo is one Homebase file, sealed by the web app the way chat-kmp does
/// it (STATUS.md, decision 2). The photo has its own random key, locked with
/// the drive key. Its details are encrypted with that key, and its original
/// and thumbnails with that key and the payload's own IV. It's uploaded in one
/// request (<see cref="UploadPath"/>), instead of Ente's upload URLs and POST
/// /files. The proxy stores and returns all of it as it is, and has no key that
/// opens any of it. Each read is one Odin call.
/// </summary>
public static class FilesHandlers
{
    /// <summary>Where the web app uploads a photo in Homebase mode.</summary>
    public const string UploadPath = "/homebase/files";

    /// <summary>The largest original accepted (one request, no multipart).</summary>
    private const long MaxUploadBytes = 1L << 30; // 1 GiB

    /// <summary>Homebase's largest thumbnail (1600 px) is capped at 640 KB; this leaves room.</summary>
    private const int MaxThumbnailBytes = 1 << 20; // 1 MiB

    /// <summary>Homebase's thumbnail ladder (chat-kmp's baseThumbSizes) is at most 4, plus one at source size.</summary>
    private const int MaxThumbnails = 6;

    private const int MaxRequestJsonBytes = 64 << 10; // 64 KiB

    private const string OneRequest =
        "Homebase mode uploads a photo in one request (POST " + UploadPath + "): the original, its thumbnail " +
        "and the record, as one Homebase file.";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(UploadPath, Upload)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes + (2 << 20)));
        app.MapGet(UploadPath + "/{fileID:long}/thumb", GetPhotoThumbnail);
        app.MapGet(UploadPath + "/{fileID:long}/original", GetPhotoOriginal);
        Todo.Map(app, "GET", "/files/preview/{fileID}",
            "Ente's thumbnail, with Ente's encryption. Homebase mode photos have none: the web app reads " +
            "GET " + UploadPath + "/{fileID}/thumb.");
        Todo.Map(app, "GET", "/files/download/{fileID}",
            "Ente's original, with Ente's encryption. Homebase mode photos have none: the web app reads " +
            "GET " + UploadPath + "/{fileID}/original.");

        Todo.Map(app, "GET", "/files/upload-urls", OneRequest);
        Todo.Map(app, "POST", "/files/upload-url", OneRequest);
        Todo.Map(app, "POST", "/files", OneRequest);
        Todo.Map(app, "GET", "/files/upload-eligibility",
            "Checks storage quota before an upload. Odin enforces quota on the upload itself (507); there's " +
            "no read-only quota check to call.");
        Todo.Map(app, "GET", "/files/multipart-upload-urls",
            "Multipart uploads would need the proxy to assemble parts (state). " + OneRequest);
        Todo.Map(app, "POST", "/files/multipart-upload-url",
            "Multipart uploads would need the proxy to assemble parts (state). " + OneRequest);
        Todo.Map(app, "GET", "/files/download/v2/{fileID}", Why.PresignedUrls);
        Todo.Map(app, "GET", "/files/download/v3/{fileID}", Why.PresignedUrls);
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


    /// <summary>
    /// POST /homebase/files: a new photo in an album, in one multipart request
    /// whose parts come in this order: "request" (JSON, below), one "thumbnail"
    /// per entry of request.thumbnails, then "file" (streamed through to Odin,
    /// never buffered). All of it is sealed or encrypted by the web app, and
    /// stored as sent.
    ///
    /// request: fileID (the web app picks it, as EnteIds.NewId does),
    /// collectionID, homebase (the photo's sealed record: its key header and
    /// its encrypted details), payloadIv (the original's own IV, which its
    /// thumbnails share), contentType, thumbnails [{pixelWidth, pixelHeight,
    /// contentType}], and previewThumbnail {pixelWidth, pixelHeight,
    /// contentType, content}.
    ///
    /// Two Odin calls (decision 4): the photo, then its membership in the album.
    /// </summary>
    private static async Task<IResult> Upload(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) ||
            HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
        {
            return EnteResults.Status(400);
        }

        var reader = new MultipartReader(boundary, context.Request.Body);

        var section = await reader.ReadNextSectionAsync(ct);
        if (Name(section) != "request" ||
            await ReadAtMost(section!.Body, MaxRequestJsonBytes, ct) is not { } requestBytes)
        {
            return EnteResults.Status(400);
        }

        JsonObject body;
        try
        {
            if (JsonNode.Parse(requestBytes) is not JsonObject parsed) return EnteResults.Status(400);
            body = parsed;
        }
        catch (JsonException)
        {
            return EnteResults.Status(400);
        }

        if (ReadUpload(body) is not { } upload) return EnteResults.Status(400);

        var thumbnails = new List<OdinThumbnail>();
        foreach (var (width, height, contentType) in upload.Thumbnails)
        {
            section = await reader.ReadNextSectionAsync(ct);
            if (Name(section) != "thumbnail" ||
                await ReadAtMost(section!.Body, MaxThumbnailBytes, ct) is not { Length: > 0 } bytes)
            {
                return EnteResults.Status(400);
            }

            thumbnails.Add(new OdinThumbnail(bytes, width, height, contentType));
        }

        section = await reader.ReadNextSectionAsync(ct);
        if (Name(section) != "file") return EnteResults.Status(400);

        var fileGuid = EnteIds.ToGuid(EnteIds.Kind.File, upload.FileID);
        var collectionGuid = EnteIds.ToGuid(EnteIds.Kind.Collection, upload.CollectionID);
        await using var original = new CountingStream(section!.Body);

        var photoResponse = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(fileGuid, EnteDrive.FileRecordFileType, upload.Sealed)
            {
                Tags = [fileGuid],
                Payload = new OdinPayload(EnteDrive.PhotoPayloadKey, original, upload.ContentType, upload.PayloadIv)
                {
                    Thumbnails = thumbnails,
                    PreviewThumbnail = upload.PreviewThumbnail,
                },
            }, ct);
        if (photoResponse.IsExistingUniqueId) return EnteResults.Status(409);
        if (!photoResponse.IsSuccess) return EnteResults.OdinFailure(photoResponse);

        var membershipResponse = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            EnteRecords.Membership(upload.CollectionID, upload.FileID), ct);
        if (!membershipResponse.IsSuccess) return EnteResults.OdinFailure(membershipResponse);

        // The answer is what was stored, in the shape of Odin's header, as the diff will return it.
        var stored = new JsonObject
        {
            ["sharedSecretEncryptedKeyHeader"] = upload.Sealed.KeyHeader.DeepClone(),
            ["fileMetadata"] = new JsonObject
            {
                ["appData"] = new JsonObject { ["content"] = upload.Sealed.Content },
                ["payloads"] = new JsonArray(new JsonObject
                {
                    ["key"] = EnteDrive.PhotoPayloadKey,
                    ["iv"] = Convert.ToBase64String(upload.PayloadIv),
                    ["bytesWritten"] = original.BytesRead,
                    ["thumbnails"] = new JsonArray([
                        .. thumbnails.Select(t => (JsonNode)new JsonObject { ["bytesWritten"] = (long)t.Bytes.Length }),
                    ]),
                }),
            },
        };
        return Results.Json(EnteRecords.File(upload.FileID, upload.CollectionID, stored, credentials,
            EnteRecords.NowMicros()));
    }

    /// <summary>A read-only stream that counts the bytes read through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            BytesRead += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record UploadRequest(
        long FileID,
        long CollectionID,
        SealedRecord Sealed,
        byte[] PayloadIv,
        string ContentType,
        IReadOnlyList<(int Width, int Height, string ContentType)> Thumbnails,
        OdinPreviewThumbnail? PreviewThumbnail);

    private static int? Pixels(JsonNode? node) =>
        EnteRecords.Long(node) is { } n and > 0 and <= 16384 ? (int)n : null;

    private static string? Text(JsonNode? node, int maxLength) =>
        node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } text &&
        text.Length <= maxLength
            ? text
            : null;

    /// <summary>A 16-byte IV, base64.</summary>
    private static byte[]? Iv(JsonNode? node)
    {
        if (Text(node, 64) is not { } text) return null;
        var buffer = new byte[48];
        return Convert.TryFromBase64String(text, buffer, out var n) && n == 16 ? buffer[..16] : null;
    }

    /// <summary>The request part, checked; null if anything is missing or out of range.</summary>
    private static UploadRequest? ReadUpload(JsonObject body)
    {
        if (EnteRecords.Long(body["fileID"]) is not { } fileID || fileID is <= 0 or > EnteIds.MaxSafeInteger) return null;
        if (EnteRecords.Long(body["collectionID"]) is not { } collectionID || collectionID is <= 0 or > EnteIds.MaxSafeInteger)
        {
            return null;
        }

        if (SealedRecord.Read(body["homebase"]) is not { } sealedRecord ||
            Iv(body["payloadIv"]) is not { } payloadIv ||
            Text(body["contentType"], 255) is not { } contentType ||
            body["thumbnails"] is not JsonArray thumbnailsJson || thumbnailsJson.Count > MaxThumbnails)
        {
            return null;
        }

        var thumbnails = new List<(int, int, string)>();
        foreach (var t in thumbnailsJson)
        {
            if (Pixels(t?["pixelWidth"]) is not { } w || Pixels(t?["pixelHeight"]) is not { } h ||
                Text(t?["contentType"], 255) is not { } type)
            {
                return null;
            }

            // Odin keys a payload's thumbnails by width, and wants each size once.
            if (thumbnails.Any(x => x.Item1 == w)) return null;
            thumbnails.Add((w, h, type));
        }

        OdinPreviewThumbnail? preview = null;
        if (body["previewThumbnail"] is JsonObject p)
        {
            if (Pixels(p["pixelWidth"]) is not { } w || Pixels(p["pixelHeight"]) is not { } h ||
                Text(p["contentType"], 255) is not { } type || Text(p["content"], 2048) is not { } content)
            {
                return null;
            }

            preview = new OdinPreviewThumbnail(w, h, type, content);
        }

        return new UploadRequest(fileID, collectionID, sealedRecord, payloadIv, contentType, thumbnails, preview);
    }

    /// <summary>A multipart section's form name, or null.</summary>
    private static string? Name(MultipartSection? section) =>
        section is not null && ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
            ? HeaderUtilities.RemoveQuotes(disposition.Name).Value
            : null;

    /// <summary>All of <paramref name="stream"/>, or null if it's longer than <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadAtMost(Stream stream, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 << 10];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Passes Odin's answer through: the bytes as stored (Homebase-encrypted by
    /// the web app), streamed, with their content type; Odin's status if it failed.
    /// </summary>
    private static async Task<IResult> PassThrough(HttpContext context, OdinClient odin,
        Auth.HomebaseCredentials credentials, string path, CancellationToken ct)
    {
        var response = await odin.GetRawAsync(credentials, EnteDrive.Alias, path, ct);
        context.Response.RegisterForDispose(response);
        if ((int)response.StatusCode == 404) return EnteResults.Status(404);
        if (!response.IsSuccessStatusCode)
        {
            return EnteResults.OdinFailure(new OdinResponse((int)response.StatusCode, null, null));
        }

        return Results.Stream(await response.Content.ReadAsStreamAsync(ct),
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }

    /// <summary>
    /// GET /homebase/files/{fileID}/thumb?width&amp;height: the photo's thumbnail
    /// nearest that size, as stored (the web app decrypts it). One Odin call.
    /// </summary>
    private static async Task<IResult> GetPhotoThumbnail(long fileID, int? width, int? height,
        HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (fileID is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(404);
        var w = Math.Clamp(width ?? 640, 1, 16384);
        var h = Math.Clamp(height ?? w, 1, 16384);
        var uid = EnteIds.ToGuid(EnteIds.Kind.File, fileID);
        return await PassThrough(context, odin, credentials,
            $"by-uid/{uid:D}/payload/{EnteDrive.PhotoPayloadKey}/thumb?width={w}&height={h}&directMatchOnly=false",
            ct);
    }

    /// <summary>
    /// GET /homebase/files/{fileID}/original: the photo's original, as stored
    /// (the web app decrypts it), streamed. One Odin call.
    /// </summary>
    private static async Task<IResult> GetPhotoOriginal(long fileID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (fileID is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(404);
        var uid = EnteIds.ToGuid(EnteIds.Kind.File, fileID);
        return await PassThrough(context, odin, credentials,
            $"by-uid/{uid:D}/payload/{EnteDrive.PhotoPayloadKey}", ct);
    }
}
