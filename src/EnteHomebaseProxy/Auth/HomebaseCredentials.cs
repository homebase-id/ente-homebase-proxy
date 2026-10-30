using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnteHomebaseProxy.Auth;

/// <summary>
/// The user's Homebase credentials, sent by the Ente client in X-Auth-Token on
/// every request: base64url of {"v":1,"identity","token","ss","dk"}. Decoded per
/// request and never stored or logged.
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

    /// <summary>16 bytes: the opened key of Ente's zero-knowledge drive.</summary>
    public byte[] DriveKey { get; }

    private HomebaseCredentials(string identity, byte[] clientAuthToken, byte[] sharedSecret, byte[] driveKey)
    {
        Identity = identity;
        ClientAuthToken = clientAuthToken;
        SharedSecret = sharedSecret;
        DriveKey = driveKey;
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
                !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
            {
                return null;
            }

            var identity = root.GetProperty("identity").GetString()?.Trim().ToLowerInvariant();
            var token = FromBase64Any(root.GetProperty("token").GetString() ?? "");
            var ss = FromBase64Any(root.GetProperty("ss").GetString() ?? "");
            var dk = FromBase64Any(root.GetProperty("dk").GetString() ?? "");

            if (identity is null || !HostnameRegex().IsMatch(identity) ||
                token.Length == 0 || ss.Length != 16 || dk.Length != 16)
            {
                return null;
            }

            return new HomebaseCredentials(identity, token, ss, dk);
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
