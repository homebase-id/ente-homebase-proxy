using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnteHomebaseProxy.Odin;

/// <summary>
/// A record the web app sealed the Homebase way (chat-kmp's model, the
/// reference client). It has its own random key (AES-128 key and IV), locked
/// with the drive key in a zkDrive key header, and its JSON is encrypted with
/// that key as the file's appData.content.
///
/// The proxy checks only the shape. It stores both parts as they are and
/// returns them as Odin gives them back, with no key that opens either.
/// JSON: {"keyHeader": {encryptionVersion, type, iv, encryptedAesKey}, "content"}.
/// </summary>
public sealed record SealedRecord(JsonObject KeyHeader, string Content)
{
    /// <summary>Largest content accepted, base64 (a record, not media).</summary>
    public const int MaxContentLength = 96 << 10;

    public JsonObject ToJson() => new()
    {
        ["keyHeader"] = KeyHeader.DeepClone(),
        ["content"] = Content,
    };

    /// <summary>A sealed record from the web app, or null if it isn't one.</summary>
    public static SealedRecord? Read(JsonNode? node)
    {
        if (node is not JsonObject obj ||
            ReadKeyHeader(obj["keyHeader"], acceptNumericType: false) is not { } keyHeader ||
            ReadContent(obj["content"]) is not { } content)
        {
            return null;
        }

        return new SealedRecord(keyHeader, content);
    }

    /// <summary>
    /// The sealed record in one of Odin's file headers: its key header (sent
    /// back as stored, since Odin can't open a zkDrive one) and its content.
    /// Null if the file has no zkDrive key header or no content.
    /// </summary>
    public static SealedRecord? FromHeader(JsonNode header)
    {
        if (ReadKeyHeader(header["sharedSecretEncryptedKeyHeader"], acceptNumericType: true) is not { } keyHeader ||
            ReadContent(header["fileMetadata"]?["appData"]?["content"]) is not { } content)
        {
            return null;
        }

        return new SealedRecord(keyHeader, content);
    }

    /// <summary>
    /// A zkDrive key header: version 1, a 16-byte IV, and 48 bytes, the
    /// AES-CBC of IV ‖ key under the drive key. Odin may write the type as
    /// its number (13).
    /// </summary>
    private static JsonObject? ReadKeyHeader(JsonNode? node, bool acceptNumericType)
    {
        if (node is not JsonObject h) return null;
        var type = h["type"];
        var isZkDrive = type?.GetValueKind() switch
        {
            JsonValueKind.String => type.GetValue<string>() == "zkDrive",
            JsonValueKind.Number => acceptNumericType && type.GetValue<int>() == 13,
            _ => false,
        };
        if (!isZkDrive ||
            h["encryptionVersion"]?.GetValueKind() != JsonValueKind.Number ||
            h["encryptionVersion"]!.GetValue<int>() != 1 ||
            Base64Length(h["iv"]) != 16 || Base64Length(h["encryptedAesKey"]) != 48)
        {
            return null;
        }

        return new JsonObject
        {
            ["encryptionVersion"] = 1,
            ["type"] = "zkDrive",
            ["iv"] = h["iv"]!.GetValue<string>(),
            ["encryptedAesKey"] = h["encryptedAesKey"]!.GetValue<string>(),
        };
    }

    /// <summary>AES-CBC ciphertext, base64: whole 16-byte blocks.</summary>
    private static string? ReadContent(JsonNode? node)
    {
        if (node?.GetValueKind() != JsonValueKind.String) return null;
        var content = node.GetValue<string>();
        if (content.Length is 0 or > MaxContentLength) return null;
        return Base64Length(node) is { } n && n > 0 && n % 16 == 0 ? content : null;
    }

    private static int? Base64Length(JsonNode? node)
    {
        if (node?.GetValueKind() != JsonValueKind.String) return null;
        var value = node.GetValue<string>();
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) ? written : null;
    }
}
