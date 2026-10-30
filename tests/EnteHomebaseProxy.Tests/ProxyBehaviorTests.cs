using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EnteHomebaseProxy.Handlers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// The proxy's own behaviour over real HTTP, with no Homebase involved: health,
/// auth failures, the TODO routes, request IDs, CORS and what gets logged.
/// </summary>
public class ProxyBehaviorTests(ProxyServer proxy) : IClassFixture<ProxyServer>
{
    private static readonly string FakeToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new
        {
            v = 1,
            identity = "frodo.example.test",
            token = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 33).ToArray()),
            ss = Convert.ToBase64String(Enumerable.Repeat((byte)0x6b, 16).ToArray()),
            dk = Convert.ToBase64String(Enumerable.Repeat((byte)0x7c, 16).ToArray()),
        })));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HealthIs200WithNoDependencies()
    {
        var response = await proxy.Client.GetAsync("/health", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/users/session-validity/v2")]
    [InlineData("PUT", "/users/attributes")]
    public async Task KeyEndpointsWithoutATokenAre401MissingToken(string method, string path)
    {
        var response = await proxy.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"missing token"}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AMalformedTokenIs401InvalidToken()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/users/session-validity/v2");
        request.Headers.Add("X-Auth-Token", "not-a-token");
        var response = await proxy.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid token"}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ATodoRouteIs501WithItsReason()
    {
        var response = await proxy.Client.PostAsync("/users/srp/create-session", null, Ct);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("NOT_IMPLEMENTED", body["code"]!.GetValue<string>());
        Assert.Contains("YouAuth", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARequestIdIsReturnedAndAPlainOneIsKept()
    {
        var fresh = await proxy.Client.GetAsync("/health", Ct);
        Assert.Matches("^[0-9a-f]{32}$", fresh.Headers.GetValues("X-Request-Id").Single());

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Request-Id", "abc-123");
        var kept = await proxy.Client.SendAsync(request, Ct);
        Assert.Equal("abc-123", kept.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task CorsAllowsTheEnteWebAppOnly()
    {
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/users/session-validity/v2");
        preflight.Headers.Add("Origin", "http://localhost:3020");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-auth-token,x-client-package");
        var allowed = await proxy.Client.SendAsync(preflight, Ct);
        Assert.Equal("http://localhost:3020", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("x-auth-token",
            string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant());

        var other = new HttpRequestMessage(HttpMethod.Get, "/health");
        other.Headers.Add("Origin", "https://evil.example");
        var refused = await proxy.Client.SendAsync(other, Ct);
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));

        var simple = new HttpRequestMessage(HttpMethod.Get, "/health");
        simple.Headers.Add("Origin", "http://localhost:3020");
        var exposed = await proxy.Client.SendAsync(simple, Ct);
        Assert.Contains("X-Request-Id", exposed.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task LogsHoldNoTokenQueryOrBody()
    {
        const string secretQuery = "ss=SECRET_QUERY_VALUE";
        const string secretBody = "SECRET_BODY_VALUE";
        var request = new HttpRequestMessage(HttpMethod.Post, $"/files/copy?{secretQuery}")
        {
            Content = new StringContent($$"""{"x":"{{secretBody}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Auth-Token", FakeToken);
        request.Headers.Add("X-Request-Id", "log-check");
        var response = await proxy.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var logs = string.Join("\n", proxy.Logs);
        Assert.Contains("log-check POST /files/copy 501", logs);
        Assert.DoesNotContain(FakeToken, logs);
        Assert.DoesNotContain("SECRET_QUERY_VALUE", logs);
        Assert.DoesNotContain(secretBody, logs);
    }

    /// <summary>
    /// Every Ente route in the five groups (from museum's main.go) is mapped:
    /// none falls through to a 404, and each TODO says why.
    /// </summary>
    [Fact]
    public void EveryEnteRouteInTheGroupsIsMapped()
    {
        var mainGo = FindUp("server/cmd/museum/main.go");
        var enteRoutes = Regex.Matches(File.ReadAllText(mainGo),
                """(publicAPI|privateAPI|storageAPI|publicCollectionAPI)\.(GET|POST|PUT|PATCH|DELETE)\("(/[^"]*)""")
            .Select(m => (Method: m.Groups[2].Value,
                Path: (m.Groups[1].Value == "publicCollectionAPI" ? "/public-collection" : "") + m.Groups[3].Value))
            .Where(r => Regex.IsMatch(r.Path, "^/(users|files|collections|trash|public-collection)(/|$)"))
            .Select(r => (r.Method, Path: Regex.Replace(r.Path, ":([A-Za-z]+)", "{$1}")))
            .ToHashSet();
        Assert.True(enteRoutes.Count > 80, $"Only {enteRoutes.Count} routes parsed from main.go");

        var mapped = proxy.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods
                              ?? []).Select(m => (Method: m, Path: "/" + e.RoutePattern.RawText!.TrimStart('/'))))
            .ToHashSet();

        var missing = enteRoutes.Where(r => !mapped.Contains(r) && !mapped.Contains((r.Method, r.Path.TrimEnd('/'))))
            .ToList();
        Assert.True(missing.Count == 0, "Not mapped: " + string.Join(", ", missing));

        var todos = proxy.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<Todo.TodoReason>()).OfType<Todo.TodoReason>().ToList();
        Assert.All(todos, t => Assert.True(t.Reason.Length > 20, t.Reason));
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(relative);
    }
}
