using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace EnteHomebaseProxy;

/// <summary>
/// Serves the Ente Photos web app (a static Next.js export, built by
/// scripts/build-web.sh) from the same site as the API, the way Ente's nginx
/// does: the file, else the file + ".html", else index.html. Only requests that
/// no API route matched and that aren't under /api.
/// </summary>
public static class WebApp
{
    public const string RootSetting = "WEB_ROOT";
    public const string DefaultRoot = "client/photos";

    /// <summary>Adds the web app, if its folder exists. Returns whether it did.</summary>
    public static bool Use(WebApplication app)
    {
        var root = app.Configuration[RootSetting] ?? Path.Combine(app.Environment.ContentRootPath, DefaultRoot);
        if (!File.Exists(Path.Combine(root, "index.html"))) return false;

        var files = new PhysicalFileProvider(Path.GetFullPath(root));
        app.Use((context, next) =>
        {
            // PathBase is only ever /api (ProxyApp).
            if (context.GetEndpoint() is null && !context.Request.PathBase.HasValue &&
                (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Request.Path = Resolve(files, context.Request.Path.Value ?? "/");
            }

            return next(context);
        });

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".webmanifest"] = "application/manifest+json";
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = file =>
            {
                // The ones Ente's own hosting sets (the _headers file) that matter here.
                file.Context.Response.Headers.XContentTypeOptions = "nosniff";
                file.Context.Response.Headers.XFrameOptions = "deny";
                // Next's hashed bundles never change; pages must be fetched fresh.
                file.Context.Response.Headers.CacheControl =
                    file.Context.Request.Path.StartsWithSegments("/_next/static")
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        });

        return true;
    }

    private static string Resolve(IFileProvider files, string path)
    {
        if (path != "/" && IsFile(files, path)) return path;
        if (path != "/" && IsFile(files, path.TrimEnd('/') + ".html")) return path.TrimEnd('/') + ".html";
        return "/index.html";
    }

    private static bool IsFile(IFileProvider files, string path)
    {
        var info = files.GetFileInfo(path);
        return info.Exists && !info.IsDirectory;
    }
}
