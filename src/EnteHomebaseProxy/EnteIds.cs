using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EnteHomebaseProxy;

/// <summary>
/// Ente's numeric IDs and Odin's uniqueIds, mapped without state (STATUS.md,
/// decision 1). A uniqueId is a type byte, then either zeros or an album ID
/// (7 bytes), then the number (8 bytes, big-endian), so the number reads
/// straight back out of the GUID.
/// </summary>
public static class EnteIds
{
    public enum Kind : byte
    {
        /// <summary>A photo's record (Ente's file).</summary>
        File = 0x01,

        /// <summary>An album (Ente's collection).</summary>
        Collection = 0x02,

        /// <summary>A photo in an album: that album's copy of the photo's key.</summary>
        Membership = 0x03,

        /// <summary>Uploaded bytes (an object an Ente file record points to).</summary>
        Object = 0x04,

        /// <summary>The key of one type of Ente "user entity" (people, locations); the number is from the type name.</summary>
        UserEntityKey = 0x05,
    }

    /// <summary>A stable number for a name: the first 6 bytes of its SHA-256.</summary>
    public static long NameNumber(string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        long n = 0;
        for (var i = 0; i < 6; i++) n = n * 256 + digest[i];
        return n == 0 ? 1 : n;
    }

    /// <summary>The largest integer JavaScript represents exactly (2^53 - 1).</summary>
    public const long MaxSafeInteger = (1L << 53) - 1;

    /// <summary>Ente's IDs fit in 7 bytes (they're below 2^53).</summary>
    private const long MaxSevenBytes = (1L << 56) - 1;

    /// <summary>
    /// A new ID: milliseconds since 1970 × 2048 + random 0–2047. Increases with
    /// creation time, as Ente's web client expects (it sorts albums by ID), and
    /// stays below 2^53 until about 2109.
    /// </summary>
    public static long NewId(DateTimeOffset? now = null)
    {
        var ms = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        return ms * 2048 + RandomNumberGenerator.GetInt32(2048);
    }

    /// <summary>The uniqueId of a file, collection or object with this ID.</summary>
    public static Guid ToGuid(Kind kind, long id) => Build(kind, 0, id);

    /// <summary>The uniqueId of "file <paramref name="fileId"/> is in collection <paramref name="collectionId"/>".</summary>
    public static Guid MembershipGuid(long collectionId, long fileId) => Build(Kind.Membership, collectionId, fileId);

    private static Guid Build(Kind kind, long middle, long id)
    {
        if (id is <= 0 or > MaxSafeInteger) throw new ArgumentOutOfRangeException(nameof(id));
        if (middle is < 0 or > MaxSevenBytes) throw new ArgumentOutOfRangeException(nameof(middle));
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)kind;
        for (var i = 7; i >= 1; i--)
        {
            bytes[i] = (byte)(middle & 0xff);
            middle >>= 8;
        }

        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], id);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The kind, the middle (album ID for memberships, else 0) and the number, or null if it isn't one of ours.</summary>
    public static (Kind Kind, long Middle, long Id)? Parse(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out _);
        var kind = (Kind)bytes[0];
        if (!Enum.IsDefined(kind)) return null;
        long middle = 0;
        for (var i = 1; i <= 7; i++) middle = (middle << 8) | bytes[i];
        var id = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        if (id is <= 0 or > MaxSafeInteger) return null;
        if (kind != Kind.Membership && middle != 0) return null;
        return (kind, middle, id);
    }

    /// <summary>
    /// Ente's user ID for an identity: the first 6 bytes of SHA-256(identity).
    /// The same as web/packages/homebase/ente.ts enteUserID.
    /// </summary>
    public static long UserId(string identity) => NameNumber(identity);

    /// <summary>The email Ente shows for an identity (web: enteEmail).</summary>
    public static string Email(string identity) => $"{identity}@homebase.invalid";
}
