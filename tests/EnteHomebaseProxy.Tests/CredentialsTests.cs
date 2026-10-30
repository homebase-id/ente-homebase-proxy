using System.Text;
using System.Text.Json;
using EnteHomebaseProxy.Auth;

namespace EnteHomebaseProxy.Tests;

public class CredentialsTests
{
    private static string Token(object value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly string Ss = Convert.ToBase64String(new byte[16]);
    private static readonly string Dk = Convert.ToBase64String(Enumerable.Repeat((byte)7, 16).ToArray());
    private static readonly string Tk = Convert.ToBase64String(Enumerable.Repeat((byte)1, 33).ToArray());

    [Fact]
    public void ParsesAGoodToken()
    {
        var credentials = HomebaseCredentials.Parse(
            Token(new { v = 1, identity = "Frodo.Example.Test", token = Tk, ss = Ss, dk = Dk }));
        Assert.NotNull(credentials);
        Assert.Equal("frodo.example.test", credentials.Identity);
        Assert.Equal(33, credentials.ClientAuthToken.Length);
        Assert.Equal(16, credentials.DriveKey.Length);
        Assert.StartsWith("Bearer ", credentials.BearerToken);
    }

    [Fact]
    public void NeverPrintsItsSecrets()
    {
        var credentials = HomebaseCredentials.Parse(
            Token(new { v = 1, identity = "frodo.example.test", token = Tk, ss = Ss, dk = Dk }))!;
        Assert.Equal("HomebaseCredentials(frodo.example.test)", credentials.ToString());
    }

    public static TheoryData<string> BadTokens => new()
    {
        "",
        "not base64 !!",
        Token(new { v = 2, identity = "frodo.example.test", token = Tk, ss = Ss, dk = Dk }),
        Token(new { v = 1, identity = "not a host", token = Tk, ss = Ss, dk = Dk }),
        Token(new { v = 1, identity = "frodo.example.test", token = "", ss = Ss, dk = Dk }),
        Token(new { v = 1, identity = "frodo.example.test", token = Tk, ss = "AAAA", dk = Dk }),
        Token(new { v = 1, identity = "frodo.example.test", token = Tk, ss = Ss }),
        Token(new[] { 1, 2, 3 }),
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public void RefusesMalformedTokens(string header) => Assert.Null(HomebaseCredentials.Parse(header));
}
