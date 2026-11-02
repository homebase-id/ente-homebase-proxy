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

    /// <summary>The web app's key header (the vector's) is what the proxy takes as sealed.</summary>
    [Fact]
    public void TheVectorsKeyHeaderIsASealedRecordsKeyHeader()
    {
        var v = Vectors["keyHeader"]!;
        var sealedRecord = SealedRecord.Read(new JsonObject
        {
            ["keyHeader"] = v["header"]!.DeepClone(),
            ["content"] = Convert.ToBase64String(new byte[32]),
        });
        Assert.NotNull(sealedRecord);
        Assert.True(JsonNode.DeepEquals(v["header"], sealedRecord.KeyHeader), sealedRecord.KeyHeader.ToJsonString());
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

    /// <summary>Odin's file headers give the type of a zkDrive key header as a name or a number.</summary>
    [Fact]
    public void ASealedRecordIsReadFromAnOdinHeader()
    {
        var v = Vectors["keyHeader"]!;
        var keyHeader = (JsonObject)v["header"]!.DeepClone();
        keyHeader["type"] = 13;
        var content = Convert.ToBase64String(new byte[48]);
        var header = new JsonObject
        {
            ["sharedSecretEncryptedKeyHeader"] = keyHeader,
            ["fileMetadata"] = new JsonObject { ["appData"] = new JsonObject { ["content"] = content } },
        };

        var sealedRecord = SealedRecord.FromHeader(header);
        Assert.NotNull(sealedRecord);
        Assert.Equal("zkDrive", sealedRecord.KeyHeader["type"]!.GetValue<string>());
        Assert.Equal(content, sealedRecord.Content);
    }

    public static TheoryData<string> NotSealed => new()
    {
        "null",
        """{"keyHeader":{"encryptionVersion":1,"type":"aes","iv":"AAAAAAAAAAAAAAAAAAAAAA==","encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"content":"AAAAAAAAAAAAAAAAAAAAAA=="}""",
        """{"keyHeader":{"encryptionVersion":1,"type":13,"iv":"AAAAAAAAAAAAAAAAAAAAAA==","encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"content":"AAAAAAAAAAAAAAAAAAAAAA=="}""",
        """{"keyHeader":{"encryptionVersion":1,"type":"zkDrive","iv":"AAAA","encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"content":"AAAAAAAAAAAAAAAAAAAAAA=="}""",
        """{"keyHeader":{"encryptionVersion":1,"type":"zkDrive","iv":"AAAAAAAAAAAAAAAAAAAAAA==","encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"content":"AAAA"}""",
        """{"keyHeader":{"encryptionVersion":1,"type":"zkDrive","iv":"AAAAAAAAAAAAAAAAAAAAAA==","encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"content":"{\"name\":\"plain\"}"}""",
    };

    /// <summary>
    /// The proxy won't store anything the web app didn't seal with the drive key:
    /// an "aes" key header (the shared secret, which Odin can open), a bad size,
    /// or plain content.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotSealed))]
    public void OnlyRecordsSealedWithTheDriveKeyAreTaken(string json) =>
        Assert.Null(SealedRecord.Read(JsonNode.Parse(json)));
}
