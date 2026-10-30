using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's /files routes (server/cmd/museum/main.go), except sharing (see
/// <see cref="SharingHandlers"/>) and trash (see <see cref="TrashHandlers"/>).
///
/// Uploads follow Ente's two phases (STATUS.md, decision 2): the client gets
/// upload URLs, PUTs each object's bytes there (here, to the proxy, with its
/// X-Auth-Token; never credentials in a URL), then creates the photo record.
/// The photo's ID is its thumbnail's object number, so a thumbnail downloads
/// in one Odin call and an original in two.
/// </summary>
public static class FilesHandlers
{
    /// <summary>Where object bytes are PUT (the proxy's stand-in for a presigned S3 URL).</summary>
    public const string ObjectsPath = "/homebase/objects";

    /// <summary>The largest object accepted (Ente's single-part uploads, decision 2).</summary>
    private const long MaxObjectBytes = 1L << 30; // 1 GiB

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/files/upload-urls", GetUploadUrls);
        app.MapPost("/files/upload-url", GetUploadUrl);
        app.MapPut(ObjectsPath + "/{objectKey:long}", PutObject)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxObjectBytes));
        app.MapPost("/files", Create);
        app.MapGet("/files/preview/{fileID:long}", GetThumbnail);
        app.MapGet("/files/download/{fileID:long}", GetFile);

        Todo.Map(app, "GET", "/files/upload-eligibility",
            "Checks storage quota before an upload. Odin enforces quota on the upload itself (507); there's " +
            "no read-only quota check to call.");
        Todo.Map(app, "GET", "/files/multipart-upload-urls",
            "Multipart uploads would need the proxy to assemble parts (state). Homebase mode uploads each " +
            "file in one PUT (decision 2).");
        Todo.Map(app, "POST", "/files/multipart-upload-url",
            "Multipart uploads would need the proxy to assemble parts (state). Homebase mode uploads each " +
            "file in one PUT (decision 2).");
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

    private static JsonObject UploadUrl(HttpContext context) => new()
    {
        ["objectKey"] = EnteIds.NewId().ToString(),
        ["url"] = $"{context.Request.Scheme}://{context.Request.Host}{ObjectsPath}/",
    };

    /// <summary>GET /files/upload-urls?count: where to PUT objects. No Odin call (decision 5).</summary>
    private static IResult GetUploadUrls(HttpContext context)
    {
        if (!EnteResults.TryGetCredentials(context, out _, out var failure)) return failure!;
        var count = Math.Clamp((int)(EnteRecords.QueryLong(context, "count") ?? 1), 1, 50);
        var urls = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var url = UploadUrl(context);
            url["url"] = url["url"]!.GetValue<string>() + url["objectKey"]!.GetValue<string>();
            urls.Add(url);
        }

        return Results.Json(new JsonObject { ["urls"] = urls });
    }

    /// <summary>POST /files/upload-url {contentLength, contentMD5}: one place to PUT an object. No Odin call.</summary>
    private static IResult GetUploadUrl(HttpContext context)
    {
        if (!EnteResults.TryGetCredentials(context, out _, out var failure)) return failure!;
        var url = UploadUrl(context);
        url["url"] = url["url"]!.GetValue<string>() + url["objectKey"]!.GetValue<string>();
        return Results.Json(url);
    }

    /// <summary>
    /// PUT /homebase/objects/{objectKey}: an object's bytes (Ente's ciphertext).
    /// One Odin call: create the object's file with the bytes as its payload,
    /// encrypted with the object's derived key (<see cref="OdinCrypto.DeriveObjectKey"/>).
    /// A repeated PUT of the same object (a client retry) is accepted as is.
    /// </summary>
    private static async Task<IResult> PutObject(long objectKey, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (objectKey is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(400);

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, ct);
        var uid = EnteIds.ToGuid(EnteIds.Kind.Object, objectKey);
        var response = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(uid, EnteDrive.ObjectFileType, Content: null)
            {
                FileKey = OdinCrypto.DeriveObjectKey(credentials.DriveKey, uid),
                Payload = (EnteDrive.ObjectPayloadKey, buffer.ToArray()),
            }, ct);
        if (response.IsSuccess || response.IsExistingUniqueId) return Results.Ok();
        return EnteResults.OdinFailure(response);
    }

    private static long? ObjectNumber(JsonNode? objectAttributes) =>
        long.TryParse(objectAttributes?["objectKey"]?.GetValue<string>(), out var n) &&
        n is > 0 and <= EnteIds.MaxSafeInteger
            ? n
            : null;

    /// <summary>
    /// POST /files: a new photo in an album, after its objects were PUT. Two
    /// Odin calls (decision 4): the photo's record, then its membership in the
    /// album (the album's copy of the photo's key). The photo's ID is its
    /// thumbnail's object number.
    /// </summary>
    private static async Task<IResult> Create(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;

        JsonObject body;
        try
        {
            if (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) is not JsonObject parsed)
            {
                return EnteResults.Status(400);
            }

            body = parsed;
        }
        catch (JsonException)
        {
            return EnteResults.Status(400);
        }

        var collectionID = body["collectionID"]?.GetValueKind() == JsonValueKind.Number
            ? body["collectionID"]!.GetValue<long>()
            : 0;
        var fileObject = ObjectNumber(body["file"]);
        var thumbnailObject = ObjectNumber(body["thumbnail"]);
        if (collectionID is <= 0 or > EnteIds.MaxSafeInteger || fileObject is null || thumbnailObject is null ||
            body["encryptedKey"] is null || body["keyDecryptionNonce"] is null || body["metadata"] is null)
        {
            return EnteResults.Status(400);
        }

        var fileID = thumbnailObject.Value;
        var ownerID = EnteIds.UserId(credentials.Identity);
        var record = new JsonObject
        {
            ["id"] = fileID,
            ["ownerID"] = ownerID,
            ["file"] = body["file"]!.DeepClone(),
            ["thumbnail"] = body["thumbnail"]!.DeepClone(),
            ["metadata"] = body["metadata"]!.DeepClone(),
            ["info"] = new JsonObject
            {
                ["fileSize"] = body["file"]?["size"]?.DeepClone(),
                ["thumbSize"] = body["thumbnail"]?["size"]?.DeepClone(),
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

        var recordResponse = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(fileGuid, EnteDrive.FileRecordFileType, record.ToJsonString()) { Tags = [fileGuid] },
            ct);
        if (recordResponse.IsExistingUniqueId) return EnteResults.Status(409);
        if (!recordResponse.IsSuccess) return EnteResults.OdinFailure(recordResponse);

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

    private static async Task<IResult> ObjectBytes(Microsoft.AspNetCore.Http.HttpContext context, OdinClient odin,
        Auth.HomebaseCredentials credentials, long objectNumber, CancellationToken ct)
    {
        var uid = EnteIds.ToGuid(EnteIds.Kind.Object, objectNumber);
        var (status, bytes) = await odin.GetPayloadByUniqueIdAsync(credentials, EnteDrive.Alias, uid,
            EnteDrive.ObjectPayloadKey, ct);
        if (status == 404) return EnteResults.Status(404);
        if (bytes is null) return EnteResults.OdinFailure(new OdinResponse(status, null, null));
        var key = OdinCrypto.DeriveObjectKey(credentials.DriveKey, uid);
        return Results.Bytes(OdinCrypto.AesCbcDecrypt(key.AesKey, key.Iv, bytes), "application/octet-stream");
    }

    /// <summary>
    /// GET /files/preview/{fileID}: the thumbnail's bytes (Ente's ciphertext).
    /// One Odin call: the photo's ID is its thumbnail's object number.
    /// </summary>
    private static Task<IResult> GetThumbnail(long fileID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure))
        {
            return Task.FromResult(failure!);
        }

        if (fileID is <= 0 or > EnteIds.MaxSafeInteger) return Task.FromResult(EnteResults.Status(404));
        return ObjectBytes(context, odin, credentials, fileID, ct);
    }

    /// <summary>
    /// GET /files/download/{fileID}: the original's bytes (Ente's ciphertext).
    /// Two Odin calls (decision 2): the photo's record, for its object, then
    /// the object.
    /// </summary>
    private static async Task<IResult> GetFile(long fileID, HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        if (fileID is <= 0 or > EnteIds.MaxSafeInteger) return EnteResults.Status(404);

        var response = await odin.GetHeaderByUniqueIdAsync(credentials, EnteDrive.Alias,
            EnteIds.ToGuid(EnteIds.Kind.File, fileID), ct);
        if (response.Status == 404) return EnteResults.Status(404);
        if (!response.IsSuccess || response.Body is null) return EnteResults.OdinFailure(response);
        if (ObjectNumber(EnteRecords.Content(response.Body, credentials)?["file"]) is not { } objectNumber)
        {
            return EnteResults.ApiError(502, "HOMEBASE_ERROR", "The photo's record has no object");
        }

        return await ObjectBytes(context, odin, credentials, objectNumber, ct);
    }
}
