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
        // People and places (Ente's user entities) aren't kept on Homebase yet,
        // so there are none to report, and the web app doesn't pull them in
        // Homebase mode. When they are, each will be a sealed record like an
        // album, and Ente's per-type entity key (locked with its master key)
        // won't be needed.
        Todo.Map(app, "GET", "/user-entity/key", Why.Entities);
        Todo.Map(app, "POST", "/user-entity/key", Why.Entities);
        app.MapGet("/user-entity/entity/diff", (HttpContext context) =>
            EnteResults.TryGetCredentials(context, out _, out var failure)
                ? Results.Json(new { diff = Array.Empty<object>() })
                : failure!);
        Todo.Map(app, "POST", "/user-entity/key/ensure", Why.Entities);
        Todo.Map(app, "POST", "/user-entity/entity",
            "Would be POST /api/v2/drives/{alias}/files: one file per entity, sealed by the web app, with " +
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
}
