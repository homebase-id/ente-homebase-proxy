using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnteHomebaseProxy.Auth;

/// <summary>
/// The user's Homebase credentials, sent by the Ente client in X-Auth-Token on
/// every request: base64url of {"v":2,"identity","token","ss"}. Decoded per
/// request and never stored or logged.
///
/// No drive key: everything stored on Ente's zero-knowledge drive is
/// encrypted and decrypted by the web app, so the proxy never needs it
/// (version 1 carried it, and is refused).
/// </summary>
public sealed partial class HomebaseCredentials
{
    public const string HeaderName = "X-Auth-Token";

    /// <summary>The identity's host name, e.g. frodo.baggins.demo.rocks.</summary>
    public string Identity { get; }

    /// <summary>The YouAuth client auth token, sent to Odin as a bearer token.</summary>
    public byte[] ClientAuthToken { get; }

    /// <summary>16 bytes: the key of Odin's shared-secret envelopes.</summary>
    public byte[] SharedSecret { get; }

    private HomebaseCredentials(string identity, byte[] clientAuthToken, byte[] sharedSecret)
    {
        Identity = identity;
        ClientAuthToken = clientAuthToken;
        SharedSecret = sharedSecret;
    }

    /// <summary>The value of Odin's Authorization header.</summary>
    public string BearerToken => $"Bearer {Convert.ToBase64String(ClientAuthToken)}";

    /// <summary>Never the secrets, so a stray log line can't leak them.</summary>
    public override string ToString() => $"HomebaseCredentials({Identity})";

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z0-9-]{2,}(:\d+)?$")]
    private static partial Regex HostnameRegex();

    /// <summary>
    /// The credentials in an X-Auth-Token value, or null if it's malformed. The
    /// reason is not reported: it would describe the secret.
    /// </summary>
    public static HomebaseCredentials? Parse(string headerValue)
    {
        try
        {
            using var json = JsonDocument.Parse(FromBase64Any(headerValue));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 2 ||
                root.TryGetProperty("dk", out _))
            {
                return null;
            }

            var identity = root.GetProperty("identity").GetString()?.Trim().ToLowerInvariant();
            var token = FromBase64Any(root.GetProperty("token").GetString() ?? "");
            var ss = FromBase64Any(root.GetProperty("ss").GetString() ?? "");

            if (identity is null || !HostnameRegex().IsMatch(identity) ||
                token.Length == 0 || ss.Length != 16)
            {
                return null;
            }

            return new HomebaseCredentials(identity, token, ss);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Standard or URL-safe base64, padded or not.</summary>
    public static byte[] FromBase64Any(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
