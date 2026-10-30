using System.Text.Json;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's /users routes (server/cmd/museum/main.go). Two are real: storing and
/// reading the key attributes, as one file on Ente's drive. Sign-in routes are
/// replaced by YouAuth; the rest are TODOs.
/// </summary>
public static class AuthHandlers
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPut("/users/attributes", SetAttributes);
        app.MapGet("/users/session-validity/v2", GetSessionValidityV2);
        app.MapGet("/users/details/v2", GetDetailsV2);
        // Nothing to do on Homebase: the client forgets its credentials (decision 5: zero calls).
        app.MapPost("/users/logout", () => Results.Ok());

        // Sign-in: YouAuth replaces all of these.
        Todo.Map(app, "POST", "/users/ott", Why.YouAuth);
        Todo.Map(app, "POST", "/users/verify-email", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/verify", Why.YouAuth);
        Todo.Map(app, "GET", "/users/two-factor/recover", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/remove", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/passkeys/begin", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/passkeys/finish", Why.YouAuth);
        Todo.Map(app, "GET", "/users/two-factor/passkeys/get-token", Why.YouAuth);
        Todo.Map(app, "GET", "/users/two-factor/recovery-status", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/passkeys/configure-recovery", Why.YouAuth);
        Todo.Map(app, "GET", "/users/two-factor/status", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/setup", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/enable", Why.YouAuth);
        Todo.Map(app, "POST", "/users/two-factor/disable", Why.YouAuth);
        Todo.Map(app, "PUT", "/users/email-mfa", Why.YouAuth);
        Todo.Map(app, "POST", "/users/srp/setup", Why.YouAuth);
        Todo.Map(app, "POST", "/users/srp/complete", Why.YouAuth);
        Todo.Map(app, "GET", "/users/srp/attributes", Why.YouAuth);
        Todo.Map(app, "POST", "/users/srp/verify-session", Why.YouAuth);
        Todo.Map(app, "POST", "/users/srp/create-session", Why.YouAuth);
        Todo.Map(app, "POST", "/users/srp/update",
            "Changes the Ente password and key attributes together. The key attributes file can't be " +
            "replaced in place (it's created once, never overwritten); an update would be a PATCH of that " +
            "file with its current versionTag, which first needs a read: two Odin calls.");

        // Keys.
        Todo.Map(app, "PUT", "/users/recovery-key",
            "Adds the recovery key fields to the key attributes. A PATCH of the key attributes file needs " +
            "its versionTag from a read first: two Odin calls.");
        Todo.Map(app, "GET", "/users/public-key",
            "Looks up another user's public key by email. " + Why.OtherUsers);
        Todo.Map(app, "POST", "/users/public-keys", "Looks up other users' public keys. " + Why.OtherUsers);

        // Account, sessions, billing.
        Todo.Map(app, "POST", "/users/event", "Client analytics events; there is nothing to send to Homebase.");
        Todo.Map(app, "GET", "/users/payment-token", Why.Account);
        Todo.Map(app, "GET", "/users/families-token", Why.Account);
        Todo.Map(app, "GET", "/users/accounts-token", Why.Account);
        Todo.Map(app, "GET", "/users/deletion-summary", Why.Account);
        Todo.Map(app, "GET", "/users/locker-usage", Why.Account);
        Todo.Map(app, "POST", "/users/change-email", Why.Account);
        Todo.Map(app, "GET", "/users/sessions", Why.Account);
        Todo.Map(app, "DELETE", "/users/session", Why.Account);
        Todo.Map(app, "GET", "/users/delete-challenge", Why.Account);
        Todo.Map(app, "DELETE", "/users/delete", Why.Account);
        Todo.Map(app, "GET", "/users/recover-account", Why.Account);
        Todo.Map(app, "POST", "/users/recover-account/validate", Why.Account);
        Todo.Map(app, "POST", "/users/recover-account", Why.Account);
    }

    /// <summary>A Homebase "plan" for Ente's UI: no Ente billing, so a large storage limit.</summary>
    private const long HomebaseStorageLimit = 1L << 40; // 1 TiB

    /// <summary>
    /// GET /users/details/v2: the identity as an Ente user. No Odin call.
    /// TODO: usage and fileCount are 0; Odin's drive storage usage would be one call.
    /// </summary>
    private static IResult GetDetailsV2(HttpContext context)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        var farFuture = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds() * 1000; // microseconds
        return Results.Json(new
        {
            email = EnteIds.Email(credentials.Identity),
            usage = 0,
            fileCount = 0,
            subscription = new
            {
                productID = "homebase",
                storage = HomebaseStorageLimit,
                expiryTime = farFuture,
                paymentProvider = "homebase",
                price = "0",
                period = "year",
            },
            storageBonus = 0,
        });
    }

    /// <summary>
    /// PUT /users/attributes, body {"keyAttributes": {...}}: keeps the key
    /// attributes, as sent, as the content of the key attributes file. One Odin
    /// call: create that file. A second call gets 403, as from Ente's server
    /// when key attributes are already set; the file is never overwritten.
    /// </summary>
    private static async Task<IResult> SetAttributes(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;

        string keyAttributes;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                !body.RootElement.TryGetProperty("keyAttributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Object)
            {
                return EnteResults.Status(400);
            }

            keyAttributes = attributes.GetRawText();
        }
        catch (JsonException)
        {
            return EnteResults.Status(400);
        }

        var response = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(EnteDrive.KeyAttributesUniqueId, EnteDrive.KeyAttributesFileType, keyAttributes), ct);
        if (response.IsSuccess) return Results.Ok();
        if (response.IsExistingUniqueId) return EnteResults.Status(403);
        return EnteResults.OdinFailure(response);
    }

    /// <summary>
    /// GET /users/session-validity/v2: {"hasSetKeys": true, "keyAttributes"} from
    /// the key attributes file, or {"hasSetKeys": false} if there's none yet. One
    /// Odin call: read that file's header by its uniqueId.
    /// </summary>
    private static async Task<IResult> GetSessionValidityV2(HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;

        var response = await odin.GetHeaderByUniqueIdAsync(credentials, EnteDrive.Alias,
            EnteDrive.KeyAttributesUniqueId, ct);
        if (response.Status == 404) return Results.Json(new { hasSetKeys = false });
        if (!response.IsSuccess || response.Body is null) return EnteResults.OdinFailure(response);

        var keyAttributes = OdinClient.DecryptContent(response.Body, credentials.DriveKey);
        if (!IsJsonObject(keyAttributes))
        {
            return EnteResults.ApiError(502, "HOMEBASE_ERROR", "The key attributes file isn't a JSON object");
        }

        // The stored JSON goes back as it was sent.
        return Results.Text($$"""{"hasSetKeys":true,"keyAttributes":{{keyAttributes}}}""", "application/json");
    }

    private static bool IsJsonObject(string? json)
    {
        if (json is null) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
