using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnteHomebaseProxy.Odin;

/// <summary>
/// Odin's own encryption around its API traffic, ported from
/// web/packages/homebase (context.ts, drive.ts) and checked against the same
/// vectors. None of this touches Ente's encryption: what Ente encrypted is
/// carried inside, unopened.
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

    /// <summary>A file's own key: AES-128 key and IV, fresh for every new file.</summary>
    public sealed record FileKey(byte[] AesKey, byte[] Iv)
    {
        public static FileKey New() => new(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));

        public override string ToString() => "FileKey(…)";
    }

    /// <summary>A zkDrive key header: AES-CBC(DrvK, file IV, IV ‖ AesKey), 48 bytes.</summary>
    public static JsonObject LockFileKey(FileKey fileKey, byte[] driveKey) => new()
    {
        ["encryptionVersion"] = 1,
        ["type"] = "zkDrive",
        ["iv"] = Convert.ToBase64String(fileKey.Iv),
        ["encryptedAesKey"] = Convert.ToBase64String(
            AesCbcEncrypt(driveKey, fileKey.Iv, [.. fileKey.Iv, .. fileKey.AesKey])),
    };

    public static FileKey OpenFileKey(JsonNode header, byte[] driveKey)
    {
        var type = header["type"];
        var isZk = type?.GetValueKind() switch
        {
            JsonValueKind.String => type.GetValue<string>() == "zkDrive",
            JsonValueKind.Number => type.GetValue<int>() == 13,
            _ => false,
        };
        if (!isZk) throw new CryptographicException("Not a zkDrive key header");

        var combined = AesCbcDecrypt(driveKey, Convert.FromBase64String(header["iv"]!.GetValue<string>()),
            Convert.FromBase64String(header["encryptedAesKey"]!.GetValue<string>()));
        if (combined.Length != 32) throw new CryptographicException("A zkDrive key header holds 32 bytes");
        return new FileKey(combined[16..], combined[..16]);
    }
}
