using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Auth;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// Test code only, never the proxy: Ente's own credentials from
/// HOMEBASE_CREDENTIALS (written by scripts/consent.mjs), the drive key opened
/// from the security context, and the X-Auth-Token an Ente client would send.
/// The tests play the web app: they seal and open records with the drive key,
/// which the proxy never gets. Never prints a value from the credentials.
/// </summary>
public sealed class LiveHomebase
{
    public const string CredentialsVariable = "HOMEBASE_CREDENTIALS";

    public required string Identity { get; init; }
    public required byte[] ClientAuthToken { get; init; }
    public required byte[] SharedSecret { get; init; }
    public required byte[] DriveKey { get; init; }

    public string XAuthToken => Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["v"] = 2,
        ["identity"] = Identity,
        ["token"] = Convert.ToBase64String(ClientAuthToken),
        ["ss"] = Convert.ToBase64String(SharedSecret),
    }.ToJsonString()))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public HomebaseCredentials Credentials => HomebaseCredentials.Parse(XAuthToken)!;

    public override string ToString() => $"LiveHomebase({Identity})";

    /// <summary>A file's own key (AES-128 key and IV), as the web app makes one.</summary>
    public sealed record FileKey(byte[] AesKey, byte[] Iv)
    {
        public static FileKey New() => new(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));

        public override string ToString() => "FileKey(…)";
    }

    /// <summary>The web app's seal (ente-homebase records.ts): a zkDrive key header and the encrypted JSON.</summary>
    public JsonObject Seal(string json, FileKey key) => new()
    {
        ["keyHeader"] = new JsonObject
        {
            ["encryptionVersion"] = 1,
            ["type"] = "zkDrive",
            ["iv"] = Convert.ToBase64String(key.Iv),
            ["encryptedAesKey"] = Convert.ToBase64String(OdinCrypto.AesCbcEncrypt(DriveKey, key.Iv,
                [.. key.Iv, .. key.AesKey])),
        },
        ["content"] = Convert.ToBase64String(OdinCrypto.AesCbcEncrypt(key.AesKey, key.Iv,
            Encoding.UTF8.GetBytes(json))),
    };

    /// <summary>Opens a sealed record's key header with the drive key.</summary>
    public FileKey OpenKey(JsonNode sealedRecord)
    {
        var header = sealedRecord["keyHeader"]!;
        var combined = OdinCrypto.AesCbcDecrypt(DriveKey, Convert.FromBase64String(header["iv"]!.GetValue<string>()),
            Convert.FromBase64String(header["encryptedAesKey"]!.GetValue<string>()));
        return new FileKey(combined[16..], combined[..16]);
    }

    /// <summary>A sealed record's JSON, opened as the web app does.</summary>
    public string Open(JsonNode sealedRecord)
    {
        var key = OpenKey(sealedRecord);
        return Encoding.UTF8.GetString(OdinCrypto.AesCbcDecrypt(key.AesKey, key.Iv,
            Convert.FromBase64String(sealedRecord["content"]!.GetValue<string>())));
    }

    /// <summary>
    /// The live identity, or a skip if HOMEBASE_CREDENTIALS isn't set. Fails
    /// (doesn't skip) if Homebase refuses the credentials.
    /// </summary>
    public static async Task<LiveHomebase> OpenOrSkipAsync(CancellationToken ct)
    {
        var path = Environment.GetEnvironmentVariable(CredentialsVariable);
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip($"{CredentialsVariable} isn't set: live tests need Ente's credentials (scripts/consent.mjs)");
        }

        var file = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        var identity = file["identity"]!.GetValue<string>();
        var token = Convert.FromBase64String(file["clientAuthToken"]!.GetValue<string>());
        var sharedSecret = Convert.FromBase64String(file["sharedSecret"]!.GetValue<string>());
        var appZkKey = Convert.FromBase64String(file["appZkKey"]!.GetValue<string>());

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{identity}/api/v2/auth/context");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Convert.ToBase64String(token));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Assert.Fail("credentials revoked: redo consent (node scripts/consent.mjs)");
        }

        Assert.True(response.IsSuccessStatusCode, $"auth/context: HTTP {(int)response.StatusCode}");
        var context = OdinCrypto.ReadResponseJson(sharedSecret, await response.Content.ReadAsStringAsync(ct))!;

        var grant = FindDriveGrants(context).FirstOrDefault(g =>
            SameGuid(g["permissionedDrive"]?["drive"]?["alias"]?.GetValue<string>(), EnteDrive.Alias));
        Assert.True(grant is not null, "These credentials have no grant for Ente's drive");
        var lockBase64 = grant["zkAppKeyEncryptedStorageKey"]?.GetValue<string>();
        Assert.True(!string.IsNullOrEmpty(lockBase64), "Ente's drive has no zero-knowledge key for this app");

        return new LiveHomebase
        {
            Identity = identity,
            ClientAuthToken = token,
            SharedSecret = sharedSecret,
            DriveKey = OpenZkLock(appZkKey, Convert.FromBase64String(lockBase64)),
        };
    }

    /// <summary>A zkLock: 12-byte IV ‖ AES-GCM ciphertext ‖ 16-byte tag.</summary>
    private static byte[] OpenZkLock(byte[] key, byte[] zkLock)
    {
        var cipher = zkLock[12..^16];
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(zkLock[..12], cipher, zkLock[^16..], plain);
        return plain;
    }

    private static IEnumerable<JsonNode> FindDriveGrants(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var grant in array.SelectMany(FindDriveGrants)) yield return grant;
                break;
            case JsonObject obj:
                if (obj.ContainsKey("permissionedDrive")) yield return obj;
                foreach (var grant in obj.Select(p => p.Value).SelectMany(FindDriveGrants)) yield return grant;
                break;
        }
    }

    private static bool SameGuid(string? a, string? b) =>
        a is not null && b is not null && Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

    /// <summary>
    /// Hard-deletes the file with this uniqueId, for cleaning up after a test.
    /// Only ever called with a file the test made.
    /// </summary>
    public async Task HardDeleteByUniqueIdAsync(Guid uniqueId, CancellationToken ct)
    {
        using var http = new HttpClient();
        var odin = new OdinClient(http);
        var header = await odin.GetHeaderByUniqueIdAsync(Credentials, EnteDrive.Alias, uniqueId, ct);
        if (header.Status == 404) return;
        Assert.True(header.IsSuccess, $"read before delete: HTTP {header.Status}");

        var body = OdinCrypto.EncryptEnvelope(SharedSecret, new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject
            {
                ["fileId"] = header.Body!["fileId"]!.GetValue<string>(),
                ["versionTag"] = header.Body!["fileMetadata"]!["versionTag"]!.GetValue<string>(),
            }),
        }.ToJsonString());
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{Identity}/api/v2/drives/{Guid.Parse(EnteDrive.Alias):D}/files/hard-delete-batch")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(Credentials.BearerToken);
        using var response = await http.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"hard-delete-batch: HTTP {(int)response.StatusCode}");
    }
}
