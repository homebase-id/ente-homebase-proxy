using System.Net;

namespace EnteHomebaseProxy.Tests;

/// <summary>The proxy with a (made up) web app build in WEB_ROOT.</summary>
public sealed class WebAppServer() : ProxyServer([$"--{WebApp.RootSetting}={Root}"])
{
    private static readonly string Root = MakeRoot();

    private static string MakeRoot()
    {
        var root = Directory.CreateTempSubdirectory("ente-web-").FullName;
        void Write(string path, string text)
        {
            var file = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        Write("index.html", "index");
        Write("gallery.html", "gallery");
        Write("homebase.html", "homebase");
        Write("homebase/callback.html", "callback");
        Write("_next/static/chunks/app.js", "js");
        return root;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// The bundled web app is served like Ente's nginx serves it, and the API
/// answers under /api on the same site.
/// </summary>
public class WebAppTests(WebAppServer proxy) : IClassFixture<WebAppServer>
{
    [Theory]
    [InlineData("/", "index")]
    [InlineData("/gallery", "gallery")]
    [InlineData("/homebase", "homebase")]
    [InlineData("/homebase/callback", "callback")]
    [InlineData("/gallery.html", "gallery")]
    [InlineData("/some/unknown/page", "index")]
    public async Task PagesAreTheFileThenTheHtmlFileThenIndex(string path, string page)
    {
        var response = await proxy.Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(page, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("deny", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task HashedBundlesAreCachedForever()
    {
        var response = await proxy.Client.GetAsync("/_next/static/chunks/app.js",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/health")]
    public async Task TheApiAnswersUnderApiAndAtTheRoot(string path)
    {
        var response = await proxy.Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ok\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnApiRouteNeverFallsBackToThePage()
    {
        var unknown = await proxy.Client.GetAsync("/api/gallery", TestContext.Current.CancellationToken);
        var noToken = await proxy.Client.GetAsync("/api/collections/v2", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public async Task UploadUrlsPointUnderApi()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/files/upload-urls?count=1");
        request.Headers.Add("X-Auth-Token", CredentialsTests.Token(new
        {
            v = 1, identity = "frodo.example.test", token = CredentialsTests.Tk, ss = CredentialsTests.Ss,
            dk = CredentialsTests.Dk,
        }));
        var response = await proxy.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"{proxy.Client.BaseAddress}api{Handlers.FilesHandlers.ObjectsPath}/", body);
    }
}
