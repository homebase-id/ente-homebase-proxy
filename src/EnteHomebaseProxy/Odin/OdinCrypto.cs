using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnteHomebaseProxy.Odin;

/// <summary>
/// Odin's transport encryption (the shared secret), ported from
/// web/packages/homebase (context.ts) and checked against the same vectors.
/// The proxy has no storage crypto: what's kept on the drive is encrypted by
/// the web app (see <see cref="SealedRecord"/>), and the proxy has no key that
/// opens it.
/// </summary>
public static class OdinCrypto
{
    /// <summary>AES-128-CBC with PKCS7.</summary>
    public static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

    public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);
    }

    /// <summary>A shared-secret envelope: {iv, data}, AES-128-CBC under the shared secret.</summary>
    public static JsonObject EncryptEnvelope(byte[] sharedSecret, string plain)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        return new JsonObject
        {
            ["iv"] = Convert.ToBase64String(iv),
            ["data"] = Convert.ToBase64String(AesCbcEncrypt(sharedSecret, iv, Encoding.UTF8.GetBytes(plain))),
        };
    }

    public static string DecryptEnvelope(byte[] sharedSecret, string ivBase64, string dataBase64) =>
        Encoding.UTF8.GetString(AesCbcDecrypt(sharedSecret, Convert.FromBase64String(ivBase64),
            Convert.FromBase64String(dataBase64)));

    /// <summary>
    /// Odin's response body: decrypted if it's a shared-secret envelope (X-SSE
    /// isn't relied on; the shape is), else as is.
    /// </summary>
    public static JsonNode? ReadResponseJson(byte[] sharedSecret, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var node = JsonNode.Parse(body);
        if (node is JsonObject obj && obj.Count == 2 &&
            obj["iv"]?.GetValueKind() == JsonValueKind.String && obj["data"]?.GetValueKind() == JsonValueKind.String)
        {
            var plain = DecryptEnvelope(sharedSecret, obj["iv"]!.GetValue<string>(), obj["data"]!.GetValue<string>());
            return string.IsNullOrEmpty(plain) ? null : JsonNode.Parse(plain);
        }

        return node;
    }

    /// <summary>
    /// The key header of an unencrypted file, as Odin's clients send it
    /// (chat-kmp lockKeyHeader with no key): an all-zero key header locked with
    /// the shared secret under the transfer IV. It locks nothing.
    /// </summary>
    public static JsonObject EmptyKeyHeader(byte[] sharedSecret, byte[] transferIv) => new()
    {
        ["encryptionVersion"] = 1,
        ["type"] = "aes",
        ["iv"] = Convert.ToBase64String(transferIv),
        ["encryptedAesKey"] = Convert.ToBase64String(AesCbcEncrypt(sharedSecret, transferIv, new byte[32])),
    };
}
