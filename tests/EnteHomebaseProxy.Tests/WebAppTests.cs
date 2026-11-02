using System.Net;

namespace EnteHomebaseProxy.Tests;

/// <summary>The proxy with a (made up) web app build in WEB_ROOT.</summary>
public sealed class WebAppServer() : ProxyServer([$"--{WebApp.RootSetting}={Root}",
    $"--{BuildVersion.ProxyCommitSetting}={ProxyCommit}"])
{
    public const string ProxyCommit = "d6cf82a0000000000000000000000000000000aa";
    public const string WebCommit = "377ee2bc709f24ebac5599fab961ba5387fe4cc1";

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
        Write(BuildVersion.WebVersionFile, $$"""{"commit":"{{WebCommit}}"}""");
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

    [Theory]
    [InlineData("/version")]
    [InlineData("/api/version")]
    public async Task VersionIsTheProxysAndTheWebAppsCommit(string path)
    {
        var response = await proxy.Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal($$"""{"proxy":"{{WebAppServer.ProxyCommit}}","web":"{{WebAppServer.WebCommit}}"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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

    // Shapes only: the proxy checks them, and never opens what's sealed.
    private const string Sealed = """
        {"keyHeader":{"encryptionVersion":1,"type":"zkDrive","iv":"AAAAAAAAAAAAAAAAAAAAAA==",
         "encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},
         "content":"AAAAAAAAAAAAAAAAAAAAAA=="}
        """;

    private const string GoodRequest = """
        {"fileID":3672856974923286,"collectionID":1,"homebase":
        """ + Sealed + """
        ,"payloadIv":"AAAAAAAAAAAAAAAAAAAAAA==","contentType":"image/jpeg",
         "thumbnails":[{"pixelWidth":320,"pixelHeight":240,"contentType":"image/webp"}]}
        """;

    // Ente's own upload request, with Ente's encryption: refused.
    private const string EnteRequest = """
        {"fileID":3672856974923286,"collectionID":1,"encryptedKey":"k","keyDecryptionNonce":"n",
         "metadata":{"encryptedData":"e","decryptionHeader":"h"},"file":{"decryptionHeader":"ente"},
         "contentType":"image/jpeg","thumbnails":[]}
        """;

    // A key header that isn't a zkDrive one (locked with the shared secret, which Odin could open).
    private const string AesKeyHeaderRequest = """
        {"fileID":3672856974923286,"collectionID":1,"homebase":{"keyHeader":{"encryptionVersion":1,"type":"aes",
         "iv":"AAAAAAAAAAAAAAAAAAAAAA==",
         "encryptedAesKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},
         "content":"AAAAAAAAAAAAAAAAAAAAAA=="},"payloadIv":"AAAAAAAAAAAAAAAAAAAAAA==","contentType":"image/jpeg",
         "thumbnails":[]}
        """;

    /// <summary>
    /// The one-request upload is under /api, and refuses, before calling Odin,
    /// parts out of order or missing, and photos not sealed the Homebase way.
    /// </summary>
    [Theory]
    [InlineData(GoodRequest, new[] { "thumbnail", "request", "file" })]
    [InlineData(GoodRequest, new[] { "request", "file", "thumbnail" })]
    [InlineData(GoodRequest, new[] { "request", "thumbnail" })]
    [InlineData(EnteRequest, new[] { "request", "file" })]
    [InlineData(AesKeyHeaderRequest, new[] { "request", "file" })]
    public async Task TheUploadIsCheckedBeforeOdin(string requestJson, string[] parts)
    {
        var form = new MultipartFormDataContent();
        foreach (var part in parts)
        {
            form.Add(part == "request" ? new StringContent(requestJson) : new ByteArrayContent([1, 2, 3]), part, part);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api{Handlers.FilesHandlers.UploadPath}")
        {
            Content = form,
        };
        request.Headers.Add("X-Auth-Token", CredentialsTests.GoodToken());
        var response = await proxy.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
