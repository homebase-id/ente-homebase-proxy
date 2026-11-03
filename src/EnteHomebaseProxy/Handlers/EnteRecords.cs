using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Auth;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's album and photo records as they're kept on Homebase (STATUS.md,
/// decisions 1, 2 and 4), and their Ente response shapes (web/packages/media/
/// collection.ts RemoteCollection, file.ts RemoteEnteFile).
///
/// - An album is one file (kind Collection), sealed by the web app.
/// - A photo is one file (kind File), sealed by the web app, with the original
///   and its thumbnails as its payload.
/// - A photo in an album is one unencrypted, empty file (kind Membership,
///   groupId = the album).
///
/// What's sealed goes to the web app as "homebase": {keyHeader, content}, which
/// it opens in place of Ente's decryption. Ente's own encrypted fields are blank,
/// because there's no Ente encryption on these records.
/// </summary>
public static class EnteRecords
{
    /// <summary>
    /// A photo in an album: unencrypted and empty. Its uniqueId names both,
    /// its groupId is the album, and its tags are the photo and the album.
    /// </summary>
    public static NewOdinFile Membership(long collectionId, long fileId) =>
        new(EnteIds.MembershipGuid(collectionId, fileId), EnteDrive.MembershipFileType, null)
        {
            GroupId = EnteIds.ToGuid(EnteIds.Kind.Collection, collectionId),
            Tags = [EnteIds.ToGuid(EnteIds.Kind.File, fileId), EnteIds.ToGuid(EnteIds.Kind.Collection, collectionId)],
        };

    /// <summary>Ente microseconds now.</summary>
    public static long NowMicros() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public static JsonObject Owner(HomebaseCredentials credentials) => new()
    {
        ["id"] = EnteIds.UserId(credentials.Identity),
        ["email"] = EnteIds.Email(credentials.Identity),
    };

    /// <summary>
    /// An album as Ente's RemoteCollection. Its type is inside the sealed
    /// record; "album" here only fills the field Ente's schema requires, and the
    /// web app replaces it when it opens the record.
    /// </summary>
    public static JsonObject Collection(long id, SealedRecord sealedRecord, HomebaseCredentials credentials,
        long updationTime, bool isDeleted = false) => new()
        {
            ["id"] = id,
            ["owner"] = Owner(credentials),
            ["encryptedKey"] = "",
            ["keyDecryptionNonce"] = "",
            ["type"] = "album",
            ["sharees"] = new JsonArray(),
            ["publicURLs"] = new JsonArray(),
            ["isDeleted"] = isDeleted,
            ["updationTime"] = updationTime,
            ["homebase"] = sealedRecord.ToJson(),
        };

    /// <summary>
    /// A photo in an album as Ente's RemoteEnteFile, from its Odin header. Its
    /// details are in the sealed record. The original's payload IV goes in
    /// Ente's per-object decryptionHeader, and the sizes come from Odin.
    /// Null if the header isn't a photo kept this way.
    /// </summary>
    public static JsonObject? File(long fileId, long collectionId, JsonNode header, HomebaseCredentials credentials,
        long updationTime)
    {
        if (SealedRecord.FromHeader(header) is not { } sealedRecord ||
            OdinClient.Payload(header, EnteDrive.PhotoPayloadKey) is not { } payload ||
            payload["iv"]?.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var objectHeader = $"{EnteDrive.PhotoFormat}:{payload["iv"]!.GetValue<string>()}";
        var thumbSize = payload["thumbnails"]?.AsArray()
            .Sum(t => t?["bytesWritten"]?.GetValue<long>() ?? 0) ?? 0;
        return new JsonObject
        {
            ["id"] = fileId,
            ["collectionID"] = collectionId,
            ["ownerID"] = EnteIds.UserId(credentials.Identity),
            ["encryptedKey"] = "",
            ["keyDecryptionNonce"] = "",
            ["file"] = new JsonObject { ["decryptionHeader"] = objectHeader },
            ["thumbnail"] = new JsonObject { ["decryptionHeader"] = objectHeader },
            ["metadata"] = new JsonObject { ["encryptedData"] = "", ["decryptionHeader"] = EnteDrive.PhotoFormat },
            ["info"] = new JsonObject
            {
                ["fileSize"] = payload["bytesWritten"]?.GetValue<long>(),
                ["thumbSize"] = thumbSize,
            },
            ["isDeleted"] = false,
            ["updationTime"] = updationTime,
            ["homebase"] = sealedRecord.ToJson(),
        };
    }

    /// <summary>
    /// A photo gone from an album (removed, trashed or deleted) as Ente's
    /// RemoteEnteFile: only its IDs and isDeleted mean anything, the rest is
    /// blank, as Ente's server scrubs it.
    /// </summary>
    public static JsonObject DeletedFile(long fileId, long collectionId, HomebaseCredentials credentials,
        long updationTime) => new()
        {
            ["id"] = fileId,
            ["collectionID"] = collectionId,
            ["ownerID"] = EnteIds.UserId(credentials.Identity),
            ["encryptedKey"] = "",
            ["keyDecryptionNonce"] = "",
            ["file"] = new JsonObject { ["decryptionHeader"] = "" },
            ["thumbnail"] = new JsonObject { ["decryptionHeader"] = "" },
            ["metadata"] = new JsonObject { ["encryptedData"] = "", ["decryptionHeader"] = "" },
            ["isDeleted"] = true,
            ["updationTime"] = updationTime,
        };

    /// <summary>A photo's entry in the trash (see <see cref="EnteDrive.TrashFileType"/>).</summary>
    public static NewOdinFile TrashEntry(long fileId, long collectionId) =>
        new(EnteIds.ToGuid(EnteIds.Kind.Trash, fileId), EnteDrive.TrashFileType, null)
        {
            GroupId = EnteIds.ToGuid(EnteIds.Kind.Collection, collectionId),
            Tags = [EnteIds.ToGuid(EnteIds.Kind.File, fileId)],
        };

    /// <summary>The Ente number of the first tag of this kind (a tombstone has no uniqueId, but keeps its tags).</summary>
    public static long? TaggedId(JsonNode header, EnteIds.Kind kind) =>
        OdinClient.Tags(header).Select(EnteIds.Parse)
            .FirstOrDefault(p => p is { } t && t.Kind == kind)?.Id;

    /// <summary>The album a membership or trash entry names in its groupId.</summary>
    public static long? GroupCollectionId(JsonNode header) =>
        Guid.TryParse(header["fileMetadata"]?["appData"]?["groupId"]?.GetValue<string>(), out var g) &&
        EnteIds.Parse(g) is { Kind: EnteIds.Kind.Collection } p
            ? p.Id
            : null;

    /// <summary>The Ente number in a header's uniqueId, if it's of this kind.</summary>
    public static (long Middle, long Id)? IdOf(JsonNode header, EnteIds.Kind kind)
    {
        var uid = header["fileMetadata"]?["appData"]?["uniqueId"]?.GetValue<string>();
        if (uid is null || !Guid.TryParse(uid, out var guid)) return null;
        var parsed = EnteIds.Parse(guid);
        return parsed is { } p && p.Kind == kind ? (p.Middle, p.Id) : null;
    }

    /// <summary>A JSON whole number, or null.</summary>
    public static long? Long(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<long>(out var n) ? n : null;

    /// <summary>An Ente ID: a whole number in 1..2^53-1, or null.</summary>
    public static long? Id(JsonNode? node) =>
        Long(node) is { } id && id is > 0 and <= EnteIds.MaxSafeInteger ? id : null;

    /// <summary>
    /// Ente IDs from a JSON array of numbers, or of objects with an "id"
    /// (CollectionFileItem), without repeats, in order; null if the array is
    /// missing, empty, longer than <paramref name="max"/>, or holds anything else.
    /// </summary>
    public static List<long>? Ids(JsonNode? node, int max)
    {
        if (node is not JsonArray array || array.Count is 0 || array.Count > max) return null;
        var ids = new List<long>();
        foreach (var item in array)
        {
            var id = item is JsonObject o ? Id(o["id"]) : Id(item);
            if (id is not { } value) return null;
            if (!ids.Contains(value)) ids.Add(value);
        }

        return ids;
    }

    /// <summary>The request body as a JSON object, or null if it isn't one.</summary>
    public static async Task<JsonObject?> BodyAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A long query parameter, or null if missing or not a number.</summary>
    public static long? QueryLong(HttpContext context, string name) =>
        long.TryParse(context.Request.Query[name].ToString(), out var value) ? value : null;
}
