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
/// A photo is one Homebase file, Homebase's way (STATUS.md, decision 2): Ente's
/// record as content, the original as the payload, and Ente's thumbnail as
/// that payload's thumbnail. In Homebase mode the web app uploads all three in
/// one request (<see cref="UploadPath"/>) instead of Ente's upload URLs and
/// POST /files. A thumbnail or an original then downloads in one Odin call.
/// </summary>
public static class FilesHandlers
{
    /// <summary>Where the web app uploads a photo in Homebase mode.</summary>
    public const string UploadPath = "/homebase/files";

    /// <summary>The largest original accepted (one request, no multipart).</summary>
    private const long MaxUploadBytes = 1L << 30; // 1 GiB

    /// <summary>Ente's thumbnails are at most 100 KB; this leaves room.</summary>
    private const int MaxThumbnailBytes = 1 << 20; // 1 MiB

    private const int MaxRequestJsonBytes = 64 << 10; // 64 KiB

    private const string OneRequest =
        "Homebase mode uploads a photo in one request (POST " + UploadPath + "): the original, its thumbnail " +
        "and the record, as one Homebase file.";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(UploadPath, Upload)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes + (2 << 20)));
        app.MapGet("/files/preview/{fileID:long}", GetThumbnail);
        app.MapGet("/files/download/{fileID:long}", GetFile);

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
    /// whose parts come in this order: "request" (Ente's POST /files body,
    /// without objectKeys), "thumbnail" (Ente's encrypted thumbnail), "file"
    /// (Ente's encrypted original, streamed through to Odin, never buffered).
    /// Two Odin calls (decision 4): the photo, then its membership in the album.
    /// The proxy picks the photo's ID.
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

        section = await reader.ReadNextSectionAsync(ct);
        if (Name(section) != "thumbnail" ||
            await ReadAtMost(section!.Body, MaxThumbnailBytes, ct) is not { Length: > 0 } thumbnail)
        {
            return EnteResults.Status(400);
        }

        section = await reader.ReadNextSectionAsync(ct);
        if (Name(section) != "file") return EnteResults.Status(400);

        var collectionID = body["collectionID"]?.GetValueKind() == JsonValueKind.Number
            ? body["collectionID"]!.GetValue<long>()
            : 0;
        if (collectionID is <= 0 or > EnteIds.MaxSafeInteger ||
            body["file"]?["decryptionHeader"] is null || body["thumbnail"]?["decryptionHeader"] is null ||
            body["encryptedKey"] is null || body["keyDecryptionNonce"] is null || body["metadata"] is null)
        {
            return EnteResults.Status(400);
        }

        var fileID = EnteIds.NewId();
        var record = new JsonObject
        {
            ["id"] = fileID,
            ["ownerID"] = EnteIds.UserId(credentials.Identity),
            ["file"] = new JsonObject
            {
                ["decryptionHeader"] = body["file"]!["decryptionHeader"]!.DeepClone(),
                ["size"] = body["file"]?["size"]?.DeepClone(),
            },
            ["thumbnail"] = new JsonObject
            {
                ["decryptionHeader"] = body["thumbnail"]!["decryptionHeader"]!.DeepClone(),
                ["size"] = thumbnail.Length,
            },
            ["metadata"] = body["metadata"]!.DeepClone(),
            ["info"] = new JsonObject
            {
                ["fileSize"] = body["file"]?["size"]?.DeepClone(),
                ["thumbSize"] = thumbnail.Length,
            },
        };
        if (body["pubMagicMetadata"] is { } pub) record["pubMagicMetadata"] = pub.DeepClone();
        if (body["magicMetadata"] is { } magic) record["magicMetadata"] = magic.DeepClone();

        var membership = new JsonObject
        {
            ["collectionID"] = collectionID,
            ["encryptedKey"] = body["encryptedKey"]!.DeepClone(),
            ["keyDecryptionNonce"] = body["keyDecryptionNonce"]!.DeepClone(),
        };

        var fileGuid = EnteIds.ToGuid(EnteIds.Kind.File, fileID);
        var collectionGuid = EnteIds.ToGuid(EnteIds.Kind.Collection, collectionID);

        var photoResponse = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(fileGuid, EnteDrive.FileRecordFileType, record.ToJsonString())
            {
                Tags = [fileGuid],
                FileKey = OdinCrypto.DeriveObjectKey(credentials.DriveKey, fileGuid),
                Payload = new OdinPayload(EnteDrive.PhotoPayloadKey, section!.Body, EnteDrive.EnteCiphertextType)
                {
                    Thumbnails =
                    [
                        new OdinThumbnail(thumbnail, EnteDrive.ThumbnailPixels, EnteDrive.ThumbnailPixels,
                            EnteDrive.EnteCiphertextType),
                    ],
                },
            }, ct);
        if (photoResponse.IsExistingUniqueId) return EnteResults.Status(409);
        if (!photoResponse.IsSuccess) return EnteResults.OdinFailure(photoResponse);

        var membershipResponse = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(EnteIds.MembershipGuid(collectionID, fileID), EnteDrive.MembershipFileType,
                membership.ToJsonString())
            {
                GroupId = collectionGuid,
                Tags = [fileGuid, collectionGuid],
            }, ct);
        if (!membershipResponse.IsSuccess) return EnteResults.OdinFailure(membershipResponse);

        return Results.Json(EnteRecords.File(record, membership, EnteRecords.NowMicros()));
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

    /// <summary>Bytes Odin returned, opened with the photo's derived key: Ente's ciphertext.</summary>
    private static IResult Opened(Auth.HomebaseCredentials credentials, Guid uid, byte[] bytes)
    {
        var key = OdinCrypto.DeriveObjectKey(credentials.DriveKey, uid);
        return Results.Bytes(OdinCrypto.AesCbcDecrypt(key.AesKey, key.Iv, bytes), "application/octet-stream");
    }

    /// <summary>
    /// GET /files/preview/{fileID}: the thumbnail's bytes (Ente's ciphertext).
    /// One Odin call: the photo's payload's thumbnail. Photos uploaded before
    /// thumbnails moved onto the payload have none, and are read from their
    /// object file instead (a second call, only for those).
    /// </summary>
    private static async Task<IResult> GetThumbnail(long fileID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (fileID is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(404);

        var uid = EnteIds.ToGuid(EnteIds.Kind.File, fileID);
        var (status, bytes) = await odin.GetThumbnailByUniqueIdAsync(credentials, EnteDrive.Alias, uid,
            EnteDrive.PhotoPayloadKey, EnteDrive.ThumbnailPixels, EnteDrive.ThumbnailPixels, ct);
        if (bytes is not null) return Opened(credentials, uid, bytes);
        if (status != 404) return EnteResults.OdinFailure(new OdinResponse(status, null, null));

        // Uploaded before the 2026-10-30 change: the photo's ID is its thumbnail's object number.
        return await LegacyObjectBytes(odin, credentials, fileID, ct);
    }

    /// <summary>
    /// GET /files/download/{fileID}: the original's bytes (Ente's ciphertext).
    /// One Odin call: the photo's payload. Photos uploaded before the 2026-10-30 change
    /// take two more: the record, for its object, then the object.
    /// </summary>
    private static async Task<IResult> GetFile(long fileID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (fileID is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(404);

        var uid = EnteIds.ToGuid(EnteIds.Kind.File, fileID);
        var (status, bytes) = await odin.GetPayloadByUniqueIdAsync(credentials, EnteDrive.Alias, uid,
            EnteDrive.PhotoPayloadKey, ct);
        if (bytes is not null) return Opened(credentials, uid, bytes);
        if (status != 404) return EnteResults.OdinFailure(new OdinResponse(status, null, null));

        var response = await odin.GetHeaderByUniqueIdAsync(credentials, EnteDrive.Alias, uid, ct);
        if (response.Status == 404) return EnteResults.Status(404);
        if (!response.IsSuccess || response.Body is null) return EnteResults.OdinFailure(response);
        if (LegacyObjectNumber(EnteRecords.Content(response.Body, credentials)?["file"]) is not { } objectNumber)
        {
            return EnteResults.Status(404);
        }

        return await LegacyObjectBytes(odin, credentials, objectNumber, ct);
    }

    private static long? LegacyObjectNumber(JsonNode? objectAttributes) =>
        long.TryParse(objectAttributes?["objectKey"]?.GetValue<string>(), out var n) &&
        n is > 0 and <= EnteIds.MaxSafeInteger
            ? n
            : null;

    /// <summary>An object file's bytes (how photos were stored before the 2026-10-30 change).</summary>
    private static async Task<IResult> LegacyObjectBytes(OdinClient odin, Auth.HomebaseCredentials credentials,
        long objectNumber, CancellationToken ct)
    {
        var uid = EnteIds.ToGuid(EnteIds.Kind.Object, objectNumber);
        var (status, bytes) = await odin.GetPayloadByUniqueIdAsync(credentials, EnteDrive.Alias, uid,
            EnteDrive.ObjectPayloadKey, ct);
        if (status == 404) return EnteResults.Status(404);
        if (bytes is null) return EnteResults.OdinFailure(new OdinResponse(status, null, null));
        return Opened(credentials, uid, bytes);
    }
}
