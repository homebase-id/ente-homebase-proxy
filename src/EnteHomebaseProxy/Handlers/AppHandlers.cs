using System.Text.Json;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente routes outside the five groups that the gallery calls when it opens.
/// None needs Homebase: they answer with no Odin call (allowed by decision 5:
/// a fixed, small number of calls, here zero), but still require credentials.
/// </summary>
public static class AppHandlers
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/user-entity/key", GetUserEntityKey);
        app.MapPost("/user-entity/key", CreateUserEntityKey);

        // TODO: user entities themselves (people, location tags) aren't stored
        // yet (creating one is a TODO), so there are none to report.
        app.MapGet("/user-entity/entity/diff", (HttpContext context) =>
            EnteResults.TryGetCredentials(context, out _, out var failure)
                ? Results.Json(new { diff = Array.Empty<object>() })
                : failure!);
        Todo.Map(app, "POST", "/user-entity/key/ensure",
            "Create-or-get of the key: a read, then maybe a create (two calls). The web client uses GET, then " +
            "POST.");
        Todo.Map(app, "POST", "/user-entity/entity",
            "Would be POST /api/v2/drives/{alias}/files: one file per entity (encrypted by the client), with " +
            "Ente's string ID as part of the uniqueId.");
        Todo.Map(app, "PUT", "/user-entity/entity", "Would be a PATCH of the entity's file by uniqueId.");
        Todo.Map(app, "DELETE", "/user-entity/entity", "Would be a soft delete of the entity's file.");

        // No sharing yet (Why.OtherUsers), so no pending removals from shared albums.
        app.MapGet("/collection-actions/pending-remove", (HttpContext context) =>
            EnteResults.TryGetCredentials(context, out _, out var failure)
                ? Results.Json(new { actions = Array.Empty<object>() })
                : failure!);
        Todo.Map(app, "GET", "/collection-actions/delete-suggestions", Why.OtherUsers);
        Todo.Map(app, "POST", "/collection-actions/reject-delete-suggestions", Why.OtherUsers);

        // Feature flags: none set for Homebase users; every flag is optional.
        app.MapGet("/remote-store/feature-flags", (HttpContext context) =>
            EnteResults.TryGetCredentials(context, out _, out var failure)
                ? Results.Json(new { })
                : failure!);

        // TODO: Ente's contacts (people you've shared with). Homebase has its own
        // contacts; until they're mapped, there are none.
        app.MapGet("/contacts/diff", (HttpContext context) =>
            EnteResults.TryGetCredentials(context, out _, out var failure)
                ? Results.Json(new { diff = Array.Empty<object>() })
                : failure!);
    }

    private static Guid UserEntityKeyGuid(string type) =>
        EnteIds.ToGuid(EnteIds.Kind.UserEntityKey, EnteIds.NameNumber(type));

    /// <summary>
    /// GET /user-entity/key?type: the key of one type of user entity, as the
    /// client stored it, or 404 (the client then creates one). One Odin call.
    /// </summary>
    private static async Task<IResult> GetUserEntityKey(HttpContext context, OdinClient odin, CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        var type = context.Request.Query["type"].ToString();
        if (string.IsNullOrEmpty(type)) return EnteResults.Status(400);

        var response = await odin.GetHeaderByUniqueIdAsync(credentials, EnteDrive.Alias, UserEntityKeyGuid(type), ct);
        if (response.Status == 404) return EnteResults.Status(404);
        if (!response.IsSuccess || response.Body is null) return EnteResults.OdinFailure(response);
        return EnteRecords.Content(response.Body, credentials) is { } key
            ? Results.Json(key)
            : EnteResults.ApiError(502, "HOMEBASE_ERROR", "The entity key file has no content");
    }

    /// <summary>
    /// POST /user-entity/key {type, encryptedKey, header}: stores the key (the
    /// client encrypted it with its master key). One Odin call; 409 if this
    /// type has one already (it's never overwritten).
    /// </summary>
    private static async Task<IResult> CreateUserEntityKey(HttpContext context, OdinClient odin,
        CancellationToken ct)
    {
        if (!EnteResults.TryGetCredentials(context, out var credentials, out var failure)) return failure!;
        JsonObject body;
        try
        {
            if (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) is not JsonObject parsed ||
                parsed["type"]?.GetValueKind() != JsonValueKind.String ||
                parsed["encryptedKey"] is null || parsed["header"] is null)
            {
                return EnteResults.Status(400);
            }

            body = parsed;
        }
        catch (JsonException)
        {
            return EnteResults.Status(400);
        }

        var stored = new JsonObject
        {
            ["type"] = body["type"]!.DeepClone(),
            ["encryptedKey"] = body["encryptedKey"]!.DeepClone(),
            ["header"] = body["header"]!.DeepClone(),
        };
        var response = await odin.CreateFileAsync(credentials, EnteDrive.Alias,
            new NewOdinFile(UserEntityKeyGuid(body["type"]!.GetValue<string>()), EnteDrive.UserEntityKeyFileType,
                stored.ToJsonString()), ct);
        if (response.IsSuccess) return Results.Ok();
        if (response.IsExistingUniqueId) return EnteResults.Status(409);
        return EnteResults.OdinFailure(response);
    }
}
