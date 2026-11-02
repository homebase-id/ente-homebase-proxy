namespace EnteHomebaseProxy;

/// <summary>
/// Ente's zero-knowledge drive and its fixed files. The values are
/// contract/ente-homebase.json (the web app's constants.ts has the same;
/// ContractTests checks both).
/// </summary>
public static class EnteDrive
{
    /// <summary>"Ente Photos", the drive Ente's app registration asks for.</summary>
    public const string Alias = "0ada68d639fd7c0c7f35e46d80665c0c";

    // Every file below that holds anything is sealed by the web app
    // (Odin.SealedRecord): the proxy stores and returns it without opening it.

    /// <summary>Ente's key attributes (its master key, locked with the hidden Ente password), sealed.</summary>
    public static readonly Guid KeyAttributesUniqueId = Guid.Parse("d269c173-71a3-4fe4-8ee4-8d4da5c929e7");

    public const int KeyAttributesFileType = 7201;

    /// <summary>An album: its type, name and settings, sealed.</summary>
    public const int CollectionFileType = 7202;

    /// <summary>
    /// A photo: its details (Ente's metadata and magic metadata), sealed. The
    /// original is the payload (<see cref="PhotoPayloadKey"/>) with its
    /// thumbnails, encrypted by the web app with the photo's key.
    /// </summary>
    public const int FileRecordFileType = 7210;

    /// <summary>
    /// A photo in an album: unencrypted, with no content. Its uniqueId names
    /// the album and photo, and its groupId is the album.
    /// </summary>
    public const int MembershipFileType = 7211;

    /// <summary>The payload key of a photo's original (Odin: 8–10 of [a-z0-9_]). Contract: photo.payloadKey.</summary>
    public const string PhotoPayloadKey = "original";

    /// <summary>
    /// Marks Ente's record of a photo as kept the Homebase way (contract:
    /// photo.format). The file and thumbnail decryptionHeader are this, ":",
    /// and the payload's IV.
    /// </summary>
    public const string PhotoFormat = "homebase-v2";

    // The password file (uniqueId c7a886cd-326a-41db-9922-e92828ed02ec, fileType
    // 7200) belongs to the web app. The proxy never reads, writes, lists or
    // deletes it, so it has no constant here.
}
