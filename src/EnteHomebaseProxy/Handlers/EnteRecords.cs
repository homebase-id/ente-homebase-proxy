using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Auth;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's album and photo records as they're kept on Homebase (STATUS.md,
/// decisions 1 and 4), and their Ente response shapes (web/packages/media/
/// collection.ts RemoteCollection, file.ts RemoteEnteFile).
///
/// - An album is one file (kind Collection): content is the collection as the
///   client sent it, plus its id.
/// - A photo is one file (kind File): content is Ente's file record without
///   the per-album key.
/// - A photo in an album is one file (kind Membership, groupId = the album):
///   content is that album's copy of the photo's key.
/// Everything inside is Ente's own ciphertext, carried as is.
/// </summary>
public static class EnteRecords
{
    /// <summary>Ente microseconds now.</summary>
    public static long NowMicros() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public static JsonObject Owner(HomebaseCredentials credentials) => new()
    {
        ["id"] = EnteIds.UserId(credentials.Identity),
        ["email"] = EnteIds.Email(credentials.Identity),
    };

    /// <summary>An album's stored content as Ente's RemoteCollection.</summary>
    public static JsonObject Collection(JsonObject stored, HomebaseCredentials credentials, long updationTime)
    {
        var collection = (JsonObject)stored.DeepClone();
        collection["owner"] = Owner(credentials);
        collection["sharees"] = new JsonArray();
        collection["publicURLs"] = new JsonArray();
        collection["isDeleted"] = false;
        collection["updationTime"] = updationTime;
        return collection;
    }

    /// <summary>A photo's record and its album membership as Ente's RemoteEnteFile.</summary>
    public static JsonObject File(JsonObject record, JsonObject membership, long updationTime)
    {
        var file = (JsonObject)record.DeepClone();
        file["collectionID"] = membership["collectionID"]!.DeepClone();
        file["encryptedKey"] = membership["encryptedKey"]!.DeepClone();
        file["keyDecryptionNonce"] = membership["keyDecryptionNonce"]!.DeepClone();
        file["isDeleted"] = false;
        file["updationTime"] = updationTime;
        return file;
    }

    /// <summary>A header's decrypted content as a JSON object, or null.</summary>
    public static JsonObject? Content(JsonNode header, HomebaseCredentials credentials)
    {
        var json = OdinClient.DecryptContent(header, credentials.DriveKey);
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Ente number in a header's uniqueId, if it's of this kind.</summary>
    public static (long Middle, long Id)? IdOf(JsonNode header, EnteIds.Kind kind)
    {
        var uid = header["fileMetadata"]?["appData"]?["uniqueId"]?.GetValue<string>();
        if (uid is null || !Guid.TryParse(uid, out var guid)) return null;
        var parsed = EnteIds.Parse(guid);
        return parsed is { } p && p.Kind == kind ? (p.Middle, p.Id) : null;
    }

    /// <summary>A long query parameter, or null if missing or not a number.</summary>
    public static long? QueryLong(HttpContext context, string name) =>
        long.TryParse(context.Request.Query[name].ToString(), out var value) ? value : null;
}
