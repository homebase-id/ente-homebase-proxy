using System.Diagnostics;
using System.Text.RegularExpressions;
using EnteHomebaseProxy.Handlers;
using EnteHomebaseProxy.Odin;
using Microsoft.AspNetCore.HttpOverrides;

namespace EnteHomebaseProxy;

/// <summary>
/// The proxy: Ente's REST API in front of the caller's own Homebase. Stateless:
/// each request carries its credentials (X-Auth-Token) and makes at most one
/// call to Odin. Middleware is only request IDs, request logging and CORS,
/// plus the web app (WebApp) when it's bundled. The API answers both at / and
/// under /api, which is where the bundled web app calls it.
/// </summary>
public static partial class ProxyApp
{
    public const string RequestIdHeader = "X-Request-Id";
    public const string DefaultUrls = "http://localhost:8095";
    public static readonly PathString ApiPathBase = "/api";

    /// <summary>Set behind a reverse proxy (Caddy) to trust its X-Forwarded-Proto and -For.</summary>
    public const string TrustForwardedHeadersSetting = "TRUST_FORWARDED_HEADERS";

    /// <summary>The headers Ente's web app sends.</summary>
    public static readonly string[] AllowedHeaders =
        ["X-Auth-Token", "X-Client-Package", "X-Client-Version", "Content-Type", "Content-MD5", RequestIdHeader];

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex RequestIdRegex();

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
            string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
        {
            builder.WebHost.UseUrls(DefaultUrls);
        }

        var origins = (builder.Configuration["ALLOWED_ORIGINS"] ?? "http://localhost:3020")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders(AllowedHeaders)
            .WithExposedHeaders(RequestIdHeader)));

        // No retry or caching handlers: one Odin call per request, as is.
        builder.Services.AddHttpClient<OdinClient>(http => http.Timeout = TimeSpan.FromSeconds(60));

        configure?.Invoke(builder);
        var app = builder.Build();

        if (app.Configuration.GetValue<bool>(TrustForwardedHeadersSetting))
        {
            // The container is only reachable from the host's Caddy, which arrives
            // from the Docker network rather than loopback.
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
        }

        app.UsePathBase(ApiPathBase);
        app.Use(RequestIdAndLogging);
        app.UseCors();
        app.UseRouting();
        WebApp.Use(app);

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        AuthHandlers.Map(app);
        FilesHandlers.Map(app);
        CollectionsHandlers.Map(app);
        SharingHandlers.Map(app);
        TrashHandlers.Map(app);
        AppHandlers.Map(app);

        return app;
    }

    /// <summary>
    /// Gives each request an ID (the caller's, if it's a plain token, else a new
    /// one), returns it in X-Request-Id, and logs method, path, status and
    /// duration. Never headers, query strings or bodies: they carry credentials.
    /// </summary>
    private static async Task RequestIdAndLogging(HttpContext context, RequestDelegate next)
    {
        var incoming = context.Request.Headers[RequestIdHeader].ToString();
        var requestId = RequestIdRegex().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.Response.Headers[RequestIdHeader] = requestId;

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("EnteHomebaseProxy");
        var path = (context.Request.PathBase + context.Request.Path).Value; // before the web app rewrites it
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            logger.LogInformation("{RequestId} {Method} {Path} {Status} {Ms} ms", requestId,
                context.Request.Method, path, context.Response.StatusCode,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
