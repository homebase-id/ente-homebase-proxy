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

/// <summary>A new zkDrive file: its appData, encrypted content, and at most one payload.</summary>
public sealed record NewOdinFile(Guid UniqueId, int FileType, string? Content)
{
    public Guid? GroupId { get; init; }
    public IReadOnlyList<Guid>? Tags { get; init; }

    /// <summary>The file key; a fresh random one unless the caller needs to know it (objects).</summary>
    public OdinCrypto.FileKey? FileKey { get; init; }

    /// <summary>One payload, encrypted with the file key and its IV (bytes as given: Ente's ciphertext).</summary>
    public (string Key, byte[] Bytes)? Payload { get; init; }
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
    /// POST /api/v2/drives/{alias}/files: a new zkDrive file. Its content and
    /// payload are encrypted with its file key, which is locked with the drive
    /// key. Odin refuses it (see <see cref="OdinResponse.IsExistingUniqueId"/>)
    /// if a file with this uniqueId exists; it never overwrites.
    /// </summary>
    public Task<OdinResponse> CreateFileAsync(HomebaseCredentials credentials, string driveAlias, NewOdinFile file,
        CancellationToken ct)
    {
        var fileKey = file.FileKey ?? OdinCrypto.FileKey.New();
        var transferIv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var payloadDescriptors = new JsonArray();
        if (file.Payload is { } p)
        {
            payloadDescriptors.Add(new JsonObject
            {
                ["payloadKey"] = p.Key,
                ["contentType"] = "application/octet-stream",
                ["iv"] = Convert.ToBase64String(fileKey.Iv),
                ["descriptorContent"] = null,
                ["previewThumbnail"] = null,
                ["thumbnails"] = new JsonArray(),
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
            ["encryptedKeyHeader"] = OdinCrypto.LockFileKey(fileKey, credentials.DriveKey),
            ["fileMetadata"] = new JsonObject
            {
                ["isEncrypted"] = true,
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
                    ["content"] = file.Content is null
                        ? null
                        : Convert.ToBase64String(OdinCrypto.AesCbcEncrypt(fileKey.AesKey, fileKey.Iv,
                            Encoding.UTF8.GetBytes(file.Content))),
                    ["previewThumbnail"] = null,
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
            form.Add(new ByteArrayContent(OdinCrypto.AesCbcEncrypt(fileKey.AesKey, fileKey.Iv, payload.Bytes))
            {
                Headers = { ContentType = new("application/octet-stream") },
            }, "payload", payload.Key);
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
    /// GET /api/v2/drives/{alias}/files/by-uid/{uid}/payload/{key}: the payload's
    /// bytes as Odin stores them (encrypted with the file key), or null with the
    /// status if Odin didn't return them.
    /// </summary>
    public async Task<(int Status, byte[]? Bytes)> GetPayloadByUniqueIdAsync(HomebaseCredentials credentials,
        string driveAlias, Guid uniqueId, string payloadKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{FilesRoot(credentials, driveAlias)}/by-uid/{uniqueId:D}/payload/{payloadKey}");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(credentials.BearerToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, null);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>A file header's appData.content, decrypted with its file key (null if it has none).</summary>
    public static string? DecryptContent(JsonNode header, byte[] driveKey)
    {
        var content = header["fileMetadata"]?["appData"]?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(content)) return null;
        var fileKey = OdinCrypto.OpenFileKey(header["sharedSecretEncryptedKeyHeader"]!, driveKey);
        return Encoding.UTF8.GetString(
            OdinCrypto.AesCbcDecrypt(fileKey.AesKey, fileKey.Iv, Convert.FromBase64String(content)));
    }

    /// <summary>A file header's last change, in Ente's microseconds.</summary>
    public static long UpdatedMicros(JsonNode header) =>
        (header["fileMetadata"]?["updated"]?.GetValue<long>() ?? 0) * 1000;
}
