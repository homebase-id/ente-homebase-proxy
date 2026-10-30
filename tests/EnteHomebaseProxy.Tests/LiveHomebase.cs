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
/// Never prints a value from the credentials.
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
        ["v"] = 1,
        ["identity"] = Identity,
        ["token"] = Convert.ToBase64String(ClientAuthToken),
        ["ss"] = Convert.ToBase64String(SharedSecret),
        ["dk"] = Convert.ToBase64String(DriveKey),
    }.ToJsonString()))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public HomebaseCredentials Credentials => HomebaseCredentials.Parse(XAuthToken)!;

    public override string ToString() => $"LiveHomebase({Identity})";

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
            Assert.Fail("credentials revoked: redo consent (node proxy/scripts/consent.mjs)");
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
