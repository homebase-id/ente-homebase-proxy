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
    /// POST /api/v2/drives/{alias}/files: a new zkDrive file whose content is
    /// <paramref name="content"/>, encrypted with a fresh file key locked with
    /// the drive key. Odin refuses it (see <see cref="OdinResponse.IsExistingUniqueId"/>)
    /// if a file with this uniqueId exists; it never overwrites.
    /// </summary>
    public Task<OdinResponse> CreateFileAsync(HomebaseCredentials credentials, string driveAlias, Guid uniqueId,
        int fileType, string content, IReadOnlyList<Guid>? tags, CancellationToken ct)
    {
        var fileKey = OdinCrypto.FileKey.New();
        var transferIv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var instructions = new JsonObject
        {
            ["transferIv"] = Convert.ToBase64String(transferIv),
            ["storageOptions"] = new JsonObject { ["storageIntent"] = "newFileOrOverwrite" },
            ["transitOptions"] = new JsonObject { ["recipients"] = new JsonArray() },
            ["manifest"] = new JsonObject { ["payloadDescriptors"] = new JsonArray() },
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
                    ["uniqueId"] = uniqueId.ToString("D"),
                    ["fileType"] = fileType,
                    ["dataType"] = 0,
                    ["userDate"] = null,
                    ["tags"] = tags is null ? null : new JsonArray([.. tags.Select(t => JsonValue.Create(t.ToString("D")))]),
                    ["groupId"] = null,
                    ["archivalStatus"] = 0,
                    ["content"] = Convert.ToBase64String(
                        OdinCrypto.AesCbcEncrypt(fileKey.AesKey, fileKey.Iv, Encoding.UTF8.GetBytes(content))),
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
        return SendAsync(credentials,
            new HttpRequestMessage(HttpMethod.Post, FilesRoot(credentials, driveAlias)) { Content = form }, ct);
    }

    /// <summary>GET /api/v2/drives/{alias}/files/by-uid/{uid}/header (404 if there's none).</summary>
    public Task<OdinResponse> GetHeaderByUniqueIdAsync(HomebaseCredentials credentials, string driveAlias,
        Guid uniqueId, CancellationToken ct) =>
        SendAsync(credentials,
            new HttpRequestMessage(HttpMethod.Get,
                $"{FilesRoot(credentials, driveAlias)}/by-uid/{uniqueId:D}/header"), ct);

    /// <summary>A file header's appData.content, decrypted with its file key (null if it has none).</summary>
    public static string? DecryptContent(JsonNode header, byte[] driveKey)
    {
        var content = header["fileMetadata"]?["appData"]?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(content)) return null;
        var fileKey = OdinCrypto.OpenFileKey(header["sharedSecretEncryptedKeyHeader"]!, driveKey);
        return Encoding.UTF8.GetString(
            OdinCrypto.AesCbcDecrypt(fileKey.AesKey, fileKey.Iv, Convert.FromBase64String(content)));
    }
}
