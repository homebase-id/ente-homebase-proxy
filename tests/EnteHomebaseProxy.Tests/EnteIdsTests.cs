using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Tests;

/// <summary>Ente's numeric IDs and Odin's uniqueIds (STATUS.md, decision 1).</summary>
public class EnteIdsTests
{
    [Fact]
    public void AnIdRoundTripsThroughItsGuid()
    {
        const long id = 3672856974923286;
        var guid = EnteIds.ToGuid(EnteIds.Kind.File, id);
        Assert.Equal("01000000-0000-0000-000d-0c71a74d1616", guid.ToString());
        Assert.Equal((EnteIds.Kind.File, 0L, id), EnteIds.Parse(guid));
    }

    [Fact]
    public void FilesAndAlbumsWithTheSameNumberDiffer()
    {
        Assert.NotEqual(EnteIds.ToGuid(EnteIds.Kind.File, 42), EnteIds.ToGuid(EnteIds.Kind.Collection, 42));
    }

    [Fact]
    public void AMembershipCarriesTheAlbumAndThePhoto()
    {
        var guid = EnteIds.MembershipGuid(collectionId: 7, fileId: 9);
        Assert.Equal("03000000-0000-0007-0000-000000000009", guid.ToString());
        Assert.Equal((EnteIds.Kind.Membership, 7L, 9L), EnteIds.Parse(guid));
    }

    [Fact]
    public void OtherGuidsAreNotOurs()
    {
        Assert.Null(EnteIds.Parse(Guid.Parse("c7a886cd-326a-41db-9922-e92828ed02ec")));
        Assert.Null(EnteIds.Parse(Guid.Empty));
    }

    [Fact]
    public void NewIdsIncreaseWithTimeAndStaySafeInJavaScript()
    {
        var earlier = EnteIds.NewId(DateTimeOffset.Parse("2026-10-30T12:00:00Z"));
        var later = EnteIds.NewId(DateTimeOffset.Parse("2026-10-30T12:00:01Z"));
        Assert.True(later > earlier);
        Assert.True(EnteIds.NewId(DateTimeOffset.Parse("2100-01-01T00:00:00Z")) <= EnteIds.MaxSafeInteger);
    }

    [Fact]
    public void TheUserIdMatchesTheWebApp()
    {
        // web/packages/homebase/ente.test.ts checks the same value.
        Assert.Equal(256750248973160, EnteIds.UserId("frodo.baggins.demo.rocks"));
    }

    [Fact]
    public void AnObjectKeyIsDerivedTheSameWayEveryTime()
    {
        var driveKey = Enumerable.Repeat((byte)7, 16).ToArray();
        var uid = EnteIds.ToGuid(EnteIds.Kind.Object, 123);
        var a = OdinCrypto.DeriveObjectKey(driveKey, uid);
        var b = OdinCrypto.DeriveObjectKey(driveKey, uid);
        var other = OdinCrypto.DeriveObjectKey(driveKey, EnteIds.ToGuid(EnteIds.Kind.Object, 124));
        Assert.Equal(a.AesKey, b.AesKey);
        Assert.Equal(a.Iv, b.Iv);
        Assert.NotEqual(a.AesKey, other.AesKey);
    }
}
