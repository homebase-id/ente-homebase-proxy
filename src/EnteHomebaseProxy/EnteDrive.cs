namespace EnteHomebaseProxy;

/// <summary>
/// Ente's zero-knowledge drive and its fixed files. The values are
/// web/packages/homebase/constants.ts; keep them the same.
/// </summary>
public static class EnteDrive
{
    /// <summary>"Ente Photos", the drive Ente's app registration asks for.</summary>
    public const string Alias = "0ada68d639fd7c0c7f35e46d80665c0c";

    /// <summary>Ente's key attributes (the master key, locked with the Ente password).</summary>
    public static readonly Guid KeyAttributesUniqueId = Guid.Parse("d269c173-71a3-4fe4-8ee4-8d4da5c929e7");

    public const int KeyAttributesFileType = 7201;

    /// <summary>An album: Ente's collection record (as the client sent it) as content.</summary>
    public const int CollectionFileType = 7202;

    /// <summary>The key of one type of Ente user entity, as Ente's client sent it.</summary>
    public const int UserEntityKeyFileType = 7205;

    /// <summary>Uploaded bytes (Ente's object), as one payload.</summary>
    public const int ObjectFileType = 7204;

    /// <summary>A photo: Ente's file record, without the per-album key.</summary>
    public const int FileRecordFileType = 7210;

    /// <summary>A photo in an album: that album's copy of the photo's key.</summary>
    public const int MembershipFileType = 7211;

    /// <summary>The payload key of an object file's bytes (Odin: 8–10 of [a-z0-9_]).</summary>
    public const string ObjectPayloadKey = "enteobj1";

    // The password file (uniqueId c7a886cd-326a-41db-9922-e92828ed02ec, fileType
    // 7200) belongs to the web app. The proxy never reads, writes, lists or
    // deletes it, so it has no constant here.
}
