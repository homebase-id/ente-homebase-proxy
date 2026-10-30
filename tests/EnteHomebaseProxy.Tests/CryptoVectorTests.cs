using System.Text;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// Odin's encryption in C#, against the vectors web/packages/homebase checks
/// its TypeScript port with (written by the Immich client's Node
/// implementation): all three agree byte for byte.
/// </summary>
public class CryptoVectorTests
{
    private static readonly JsonNode Vectors =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")))!;

    private static byte[] B(JsonNode? node) => Convert.FromBase64String(node!.GetValue<string>());

    [Fact]
    public void AesCbcMatchesTheVector()
    {
        var v = Vectors["aesCbc"]!;
        Assert.Equal(B(v["cipher"]), OdinCrypto.AesCbcEncrypt(B(v["key"]), B(v["iv"]), B(v["plain"])));
        Assert.Equal(B(v["plain"]), OdinCrypto.AesCbcDecrypt(B(v["key"]), B(v["iv"]), B(v["cipher"])));
    }

    [Fact]
    public void KeyHeaderMatchesTheVector()
    {
        var v = Vectors["keyHeader"]!;
        var fileKey = new OdinCrypto.FileKey(B(v["aesKey"]), B(v["iv"]));
        var header = OdinCrypto.LockFileKey(fileKey, B(v["driveKey"]));
        Assert.True(JsonNode.DeepEquals(v["header"], header), header.ToJsonString());

        var opened = OdinCrypto.OpenFileKey(v["header"]!, B(v["driveKey"]));
        Assert.Equal(fileKey.AesKey, opened.AesKey);
        Assert.Equal(fileKey.Iv, opened.Iv);
    }

    [Fact]
    public void EnvelopeMatchesTheVector()
    {
        var v = Vectors["envelope"]!;
        var envelope = v["envelope"]!;
        Assert.Equal(v["plain"]!.GetValue<string>(), OdinCrypto.DecryptEnvelope(B(v["sharedSecret"]),
            envelope["iv"]!.GetValue<string>(), envelope["data"]!.GetValue<string>()));
    }

    [Fact]
    public void EnvelopeRoundTripsAndIsReadAsJson()
    {
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var envelope = OdinCrypto.EncryptEnvelope(secret, """{"fileId":"abc"}""");
        Assert.Equal("abc", OdinCrypto.ReadResponseJson(secret, envelope.ToJsonString())!["fileId"]!.GetValue<string>());
        // Plain JSON passes through.
        Assert.Equal(7, OdinCrypto.ReadResponseJson(secret, """{"n":7}""")!["n"]!.GetValue<int>());
    }

    [Fact]
    public void FileContentRoundTripsThroughTheKeyHeader()
    {
        var driveKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var fileKey = OdinCrypto.FileKey.New();
        const string content = """{"kekSalt":"abc"}""";
        var header = new JsonObject
        {
            ["sharedSecretEncryptedKeyHeader"] = OdinCrypto.LockFileKey(fileKey, driveKey),
            ["fileMetadata"] = new JsonObject
            {
                ["appData"] = new JsonObject
                {
                    ["content"] = Convert.ToBase64String(
                        OdinCrypto.AesCbcEncrypt(fileKey.AesKey, fileKey.Iv, Encoding.UTF8.GetBytes(content))),
                },
            },
        };
        Assert.Equal(content, OdinClient.DecryptContent(header, driveKey));
    }
}
