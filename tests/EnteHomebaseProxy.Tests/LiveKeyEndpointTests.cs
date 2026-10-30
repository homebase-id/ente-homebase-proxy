using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// The key endpoints through the running proxy against a real identity (no
/// mocks). Skipped without HOMEBASE_CREDENTIALS.
///
/// The key attributes file is real state on Ente's drive, so these run only
/// when there isn't one (it may be Todd's real one), and delete the one they
/// made afterwards, by its fixed uniqueId. Nothing else on the drive is
/// touched, and never the password file.
/// </summary>
public class LiveKeyEndpointTests(ProxyServer proxy) : IClassFixture<ProxyServer>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string B64(int length) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

    /// <summary>The shape of Ente's KeyAttributes (server/ente/user.go), with random test values.</summary>
    private static JsonObject SampleKeyAttributes() => new()
    {
        ["kekSalt"] = B64(16),
        ["encryptedKey"] = B64(48),
        ["keyDecryptionNonce"] = B64(24),
        ["publicKey"] = B64(32),
        ["encryptedSecretKey"] = B64(48),
        ["secretKeyDecryptionNonce"] = B64(24),
        ["memLimit"] = 67108864,
        ["opsLimit"] = 2,
        ["masterKeyEncryptedWithRecoveryKey"] = B64(48),
        ["masterKeyDecryptionNonce"] = B64(24),
        ["recoveryKeyEncryptedWithMasterKey"] = B64(48),
        ["recoveryKeyDecryptionNonce"] = B64(24),
    };

    private HttpRequestMessage Request(HttpMethod method, string path, LiveHomebase live, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Auth-Token", live.XAuthToken);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task KeyAttributesAreStoredOnceAndReadBackAsSent()
    {
        var live = await LiveHomebase.OpenOrSkipAsync(Ct);

        using (var http = new HttpClient())
        {
            var existing = await new OdinClient(http).GetHeaderByUniqueIdAsync(live.Credentials, EnteDrive.Alias,
                EnteDrive.KeyAttributesUniqueId, Ct);
            if (existing.IsSuccess)
            {
                Assert.Skip("A key attributes file already exists on Ente's drive (it may be Todd's real one); " +
                            "not touching it");
            }

            Assert.Equal(404, existing.Status);
        }

        try
        {
            var before = await proxy.Client.SendAsync(
                Request(HttpMethod.Get, "/users/session-validity/v2", live), Ct);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"hasSetKeys":false}"""),
                JsonNode.Parse(await before.Content.ReadAsStringAsync(Ct))));

            var keyAttributes = SampleKeyAttributes();
            var put = await proxy.Client.SendAsync(Request(HttpMethod.Put, "/users/attributes", live,
                new JsonObject { ["keyAttributes"] = keyAttributes.DeepClone() }.ToJsonString()), Ct);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var after = await proxy.Client.SendAsync(
                Request(HttpMethod.Get, "/users/session-validity/v2", live), Ct);
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            var body = JsonNode.Parse(await after.Content.ReadAsStringAsync(Ct))!;
            Assert.True(body["hasSetKeys"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(keyAttributes, body["keyAttributes"]),
                "keyAttributes didn't come back as sent");

            var second = await proxy.Client.SendAsync(Request(HttpMethod.Put, "/users/attributes", live,
                new JsonObject { ["keyAttributes"] = SampleKeyAttributes() }.ToJsonString()), Ct);
            Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);

            // Still the first one: never overwritten.
            var still = JsonNode.Parse(await (await proxy.Client.SendAsync(
                Request(HttpMethod.Get, "/users/session-validity/v2", live), Ct)).Content.ReadAsStringAsync(Ct))!;
            Assert.True(JsonNode.DeepEquals(keyAttributes, still["keyAttributes"]));
        }
        finally
        {
            await live.HardDeleteByUniqueIdAsync(EnteDrive.KeyAttributesUniqueId, CancellationToken.None);
        }

        var gone = await proxy.Client.SendAsync(Request(HttpMethod.Get, "/users/session-validity/v2", live), Ct);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"hasSetKeys":false}"""),
            JsonNode.Parse(await gone.Content.ReadAsStringAsync(Ct))));
    }

    [Fact]
    public async Task HomebaseRefusingTheTokenIs401()
    {
        var live = await LiveHomebase.OpenOrSkipAsync(Ct);
        var forged = new LiveHomebase
        {
            Identity = live.Identity,
            ClientAuthToken = RandomNumberGenerator.GetBytes(33),
            SharedSecret = RandomNumberGenerator.GetBytes(16),
            DriveKey = RandomNumberGenerator.GetBytes(16),
        };
        var response = await proxy.Client.SendAsync(
            Request(HttpMethod.Get, "/users/session-validity/v2", forged), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid token"}""", await response.Content.ReadAsStringAsync(Ct));
    }
}
