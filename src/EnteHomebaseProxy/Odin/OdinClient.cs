using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Auth;

namespace EnteHomebaseProxy.Odin;

/// <summary>What Odin answered: the status, its errorCode if any, and the (decrypted) body.</summary>
public sealed record OdinResponse(int Status, string? ErrorCode, JsonNode? Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>Odin refused a create because a file with that uniqueId exists.</summary>
    public bool IsExistingUniqueId =>
        Status == 400 && ErrorCode is "existingFileWithUniqueId" or "4105";
}

/// <summary>
/// A thumbnail of a payload, Homebase's way (odin-js buildManifest/buildFormData):
/// sent with the payload, keyed by payload key + width, encrypted with the
/// file key and the payload's IV.
/// </summary>
public sealed record OdinThumbnail(byte[] Bytes, int PixelWidth, int PixelHeight, string ContentType)
{
    public string Key(string payloadKey) => payloadKey + PixelWidth;
}

/// <summary>Homebase's tiny inline preview (odin-js EmbeddedThumb): stored as is, in the file's metadata.</summary>
public sealed record OdinPreviewThumbnail(int PixelWidth, int PixelHeight, string ContentType, string Content)
{
    public JsonObject ToJson() => new()
    {
        ["pixelWidth"] = PixelWidth,
        ["pixelHeight"] = PixelHeight,
        ["contentType"] = ContentType,
        ["content"] = Content,
    };
}

/// <summary>
/// One payload with its thumbnails, already encrypted by the web app the
/// Homebase way: the file's key with this payload's own IV, which its
/// thumbnails share. Sent as given and streamed, never buffered.
/// </summary>
public sealed record OdinPayload(string Key, Stream Bytes, string ContentType, byte[] Iv)
{
    public IReadOnlyList<OdinThumbnail> Thumbnails { get; init; } = [];
    public OdinPreviewThumbnail? PreviewThumbnail { get; init; }
}

/// <summary>
/// A new file on Ente's drive. With <see cref="Sealed"/> it's encrypted, by
/// the web app: Odin stores its key header and content as given. Without
/// it, the file is unencrypted and has no content. That's only for files
/// whose appData (uniqueId, groupId, tags) is all they hold.
/// </summary>
public sealed record NewOdinFile(Guid UniqueId, int FileType, SealedRecord? Sealed)
{
    public Guid? GroupId { get; init; }
    public IReadOnlyList<Guid>? Tags { get; init; }

    /// <summary>At most one payload, which needs <see cref="Sealed"/> (its key).</summary>
    public OdinPayload? Payload { get; init; }
}

/// <summary>
/// Calls to an identity's V2 drive API (odin-core UnifiedV2). Each method is
/// one HTTP request: no retries, no caching. The identity comes from the
/// request's credentials, so every call goes to the caller's own Homebase.
/// </summary>
public sealed class OdinClient(HttpClient http)
{
    private static string FilesRoot(HomebaseCredentials credentials, string driveAlias) =>
        $"https://{credentials.Identity}/api/v2/drives/{Guid.Parse(driveAlias):D}/files";

    private async Task<OdinResponse> SendAsync(HomebaseCredentials credentials, HttpRequestMessage request,
        CancellationToken ct)
    {
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(credentials.BearerToken);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return new OdinResponse(status, null, OdinCrypto.ReadResponseJson(credentials.SharedSecret, body));
        }

        return new OdinResponse(status, ReadErrorCode(body), null);
    }

    /// <summary>Odin's problem details carry errorCode as a name or a number.</summary>
    private static string? ReadErrorCode(string body)
    {
        try
        {
            var code = JsonNode.Parse(body)?["errorCode"];
            return code?.GetValueKind() switch
            {
                JsonValueKind.String => code.GetValue<string>(),
                JsonValueKind.Number => code.ToJsonString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// POST /api/v2/drives/{alias}/files: a new file. Its key header, content
    /// and payload are sent as the web app sealed them (see <see cref="NewOdinFile"/>);
    /// only the upload's own metadata is encrypted here, with the shared
    /// secret, as Odin's transport requires. Odin refuses it (see
    /// <see cref="OdinResponse.IsExistingUniqueId"/>) if a file with this
    /// uniqueId exists; it never overwrites.
    /// </summary>
    public Task<OdinResponse> CreateFileAsync(HomebaseCredentials credentials, string driveAlias, NewOdinFile file,
        CancellationToken ct)
    {
        if (file.Payload is not null && file.Sealed is null)
        {
            throw new ArgumentException("A payload needs a sealed file: its key is in the key header", nameof(file));
        }

        var transferIv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var payloadDescriptors = new JsonArray();
        if (file.Payload is { } p)
        {
            payloadDescriptors.Add(new JsonObject
            {
                ["payloadKey"] = p.Key,
                ["contentType"] = p.ContentType,
                ["iv"] = Convert.ToBase64String(p.Iv),
                ["descriptorContent"] = null,
                ["previewThumbnail"] = p.PreviewThumbnail?.ToJson(),
                ["thumbnails"] = new JsonArray([.. p.Thumbnails.Select(t => new JsonObject
                {
                    ["thumbnailKey"] = t.Key(p.Key),
                    ["pixelWidth"] = t.PixelWidth,
                    ["pixelHeight"] = t.PixelHeight,
                    ["contentType"] = t.ContentType,
                })]),
            });
        }

        var instructions = new JsonObject
        {
            ["transferIv"] = Convert.ToBase64String(transferIv),
            ["storageOptions"] = new JsonObject { ["storageIntent"] = "newFileOrOverwrite" },
            ["transitOptions"] = new JsonObject { ["recipients"] = new JsonArray() },
            ["manifest"] = new JsonObject { ["payloadDescriptors"] = payloadDescriptors },
        };
        var descriptor = new JsonObject
        {
            ["encryptedKeyHeader"] = file.Sealed?.KeyHeader.DeepClone() ??
                                     OdinCrypto.EmptyKeyHeader(credentials.SharedSecret, transferIv),
            ["fileMetadata"] = new JsonObject
            {
                ["isEncrypted"] = file.Sealed is not null,
                ["allowDistribution"] = false,
                ["accessControlList"] = new JsonObject { ["requiredSecurityGroup"] = "owner" },
                ["appData"] = new JsonObject
                {
                    ["uniqueId"] = file.UniqueId.ToString("D"),
                    ["fileType"] = file.FileType,
                    ["dataType"] = 0,
                    ["userDate"] = null,
                    ["tags"] = file.Tags is null
                        ? null
                        : new JsonArray([.. file.Tags.Select(t => JsonValue.Create(t.ToString("D")))]),
                    ["groupId"] = file.GroupId?.ToString("D"),
                    ["archivalStatus"] = 0,
                    ["content"] = file.Sealed?.Content,
                    ["previewThumbnail"] = file.Payload?.PreviewThumbnail?.ToJson(),
                },
                ["versionTag"] = null,
            },
        };
        var metadata = OdinCrypto.AesCbcEncrypt(credentials.SharedSecret, transferIv,
            Encoding.UTF8.GetBytes(descriptor.ToJsonString()));

        var form = new MultipartFormDataContent
        {
            { new StringContent(instructions.ToJsonString(), Encoding.UTF8, "application/json"), "instructions" },
            { new ByteArrayContent(metadata) { Headers = { ContentType = new("application/octet-stream") } }, "metadata" },
        };
        if (file.Payload is { } payload)
        {
            foreach (var thumbnail in payload.Thumbnails)
            {
                form.Add(new ByteArrayContent(thumbnail.Bytes) { Headers = { ContentType = new(thumbnail.ContentType) } },
                    "thumbnail", thumbnail.Key(payload.Key));
            }

            // Last, and streamed: an original can be a large video.
            form.Add(new StreamContent(payload.Bytes) { Headers = { ContentType = new(payload.ContentType) } },
                "payload", payload.Key);
        }

        return SendAsync(credentials,
            new HttpRequestMessage(HttpMethod.Post, FilesRoot(credentials, driveAlias)) { Content = form }, ct);
    }

    /// <summary>GET /api/v2/drives/{alias}/files/by-uid/{uid}/header (404 if there's none).</summary>
    public Task<OdinResponse> GetHeaderByUniqueIdAsync(HomebaseCredentials credentials, string driveAlias,
        Guid uniqueId, CancellationToken ct) =>
        SendAsync(credentials,
            new HttpRequestMessage(HttpMethod.Get,
                $"{FilesRoot(credentials, driveAlias)}/by-uid/{uniqueId:D}/header"), ct);

    /// <summary>
    /// POST /api/v2/drives/{alias}/files/query-batch: the headers (with
    /// encrypted content) of the active files matching
    /// <paramref name="queryParams"/>, newest change first.
    /// </summary>
    public async Task<(OdinResponse Response, IReadOnlyList<JsonNode> Headers)> QueryBatchAsync(
        HomebaseCredentials credentials, string driveAlias, JsonObject queryParams, int maxRecords,
        CancellationToken ct)
    {
        var body = OdinCrypto.EncryptEnvelope(credentials.SharedSecret, new JsonObject
        {
            ["queryParams"] = queryParams,
            ["resultOptionsRequest"] = new JsonObject
            {
                ["cursorState"] = null,
                ["maxRecords"] = maxRecords,
                ["includeMetadataHeader"] = true,
                ["ordering"] = "newestFirst",
                ["sorting"] = "anyChangeDate",
            },
        }.ToJsonString());
        var response = await SendAsync(credentials,
            new HttpRequestMessage(HttpMethod.Post, $"{FilesRoot(credentials, driveAlias)}/query-batch")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            }, ct);
        var headers = response.Body?["searchResults"]?.AsArray().OfType<JsonNode>().ToList() ?? [];
        return (response, headers);
    }

    /// <summary>
    /// GET <paramref name="pathAfterFiles"/> under the drive's files, Odin's
    /// response as is (status, headers, body unread), for passing bytes through.
    /// The caller disposes it.
    /// </summary>
    public async Task<HttpResponseMessage> GetRawAsync(HomebaseCredentials credentials, string driveAlias,
        string pathAfterFiles, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{FilesRoot(credentials, driveAlias)}/{pathAfterFiles}");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(credentials.BearerToken);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>A payload's descriptor in a file header, or null if the file has none by that key.</summary>
    public static JsonNode? Payload(JsonNode header, string payloadKey) =>
        header["fileMetadata"]?["payloads"]?.AsArray()
            .FirstOrDefault(p => p?["key"]?.GetValue<string>() == payloadKey);

    /// <summary>A file header's last change, in Ente's microseconds.</summary>
    public static long UpdatedMicros(JsonNode header) =>
        (header["fileMetadata"]?["updated"]?.GetValue<long>() ?? 0) * 1000;
}
