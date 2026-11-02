using System.Text.Json.Nodes;

namespace EnteHomebaseProxy;

/// <summary>
/// GET /version: what's deployed. The proxy's commit comes from the image
/// (PROXY_COMMIT, a Docker build argument the deploy workflow sets), and the
/// web app's from version.json, which scripts/build-web.sh writes into the
/// build it bundles. Each is null when unknown (a local run, or no web app).
/// </summary>
public static class BuildVersion
{
    public const string ProxyCommitSetting = "PROXY_COMMIT";
    public const string WebVersionFile = "version.json";

    public static void Map(WebApplication app)
    {
        var proxyCommit = Commit(app.Configuration[ProxyCommitSetting]);
        var webCommit = Commit(ReadWebCommit(WebApp.Root(app)));
        app.MapGet("/version", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { proxy = proxyCommit, web = webCommit });
        });
    }

    private static string? ReadWebCommit(string webRoot)
    {
        try
        {
            var file = Path.Combine(webRoot, WebVersionFile);
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?["commit"]?.GetValue<string>() : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A full git commit hash, or null.</summary>
    private static string? Commit(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? value : null;
}
