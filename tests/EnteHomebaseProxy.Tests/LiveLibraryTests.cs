using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Handlers;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// One photo through everything the gallery does to it, through the running
/// proxy, against a real identity (no mocks): another album, an edit, a
/// rename, removed, moved, trashed, restored, deleted for good, and its albums
/// deleted. Skipped without HOMEBASE_CREDENTIALS. Deletes what it made
/// afterwards, by its own uniqueIds and tags, and touches nothing else.
/// </summary>
public class LiveLibraryTests(ProxyServer proxy) : IClassFixture<ProxyServer>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private LiveHomebase live = null!;

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? json = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = json is null ? null : new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Auth-Token", live.XAuthToken);
        return request;
    }

    private async Task<JsonNode> Json(HttpMethod method, string path, JsonNode? json = null)
    {
        var response = await proxy.Client.SendAsync(Request(method, path, json), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {path}: {response.StatusCode} {body}");
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
    }

    private async Task<HttpStatusCode> Status(HttpMethod method, string path, JsonNode? json = null)
    {
        using var response = await proxy.Client.SendAsync(Request(method, path, json), Ct);
        return response.StatusCode;
    }

    private async Task<long> NewAlbum(string name) =>
        (await Json(HttpMethod.Post, "/collections", new JsonObject
        {
            ["homebase"] = live.Seal($$"""{"type":"album","name":"{{name}}"}""", LiveHomebase.FileKey.New()),
        }))["collection"]!["id"]!.GetValue<long>();

    /// <summary>The album's diff entry for the photo (since 0), or null if it has none.</summary>
    private async Task<JsonNode?> InAlbum(long collectionId, long fileId) =>
        (await Json(HttpMethod.Get, $"/collections/v2/diff?collectionID={collectionId}&sinceTime=0"))["diff"]!
        .AsArray().SingleOrDefault(f => f!["id"]!.GetValue<long>() == fileId);

    private async Task<JsonNode?> InTrash(long fileId) =>
        (await Json(HttpMethod.Get, "/trash/v2/diff?sinceTime=0"))["diff"]!
        .AsArray().SingleOrDefault(t => t!["file"]!["id"]!.GetValue<long>() == fileId);

    private static JsonObject Files(long fileId) =>
        new() { ["id"] = fileId, ["encryptedKey"] = "x", ["keyDecryptionNonce"] = "x" };

    [Fact]
    public async Task APhotoGoesThroughEverythingTheGalleryDoesToIt()
    {
        live = await LiveHomebase.OpenOrSkipAsync(Ct);
        var fileId = EnteIds.NewId();
        var albums = new List<long>();
        try
        {
            var first = await NewAlbum("library test");
            albums.Add(first);
            var second = await NewAlbum("library test 2");
            albums.Add(second);

            // Uploaded into the first album.
            var key = LiveHomebase.FileKey.New();
            var payloadIv = RandomNumberGenerator.GetBytes(16);
            var original = RandomNumberGenerator.GetBytes(50_000);
            const string details = """{"metadata":{"title":"lib.jpg","fileType":0,"creationTime":1}}""";
            var form = new MultipartFormDataContent
            {
                {
                    new StringContent(new JsonObject
                    {
                        ["fileID"] = fileId, ["collectionID"] = first, ["homebase"] = live.Seal(details, key),
                        ["payloadIv"] = Convert.ToBase64String(payloadIv), ["contentType"] = "image/jpeg",
                        ["thumbnails"] = new JsonArray(),
                    }.ToJsonString()),
                    "request"
                },
                {
                    new ByteArrayContent(OdinCrypto.AesCbcEncrypt(key.AesKey, payloadIv, original))
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                    },
                    "file", "file"
                },
            };
            var upload = new HttpRequestMessage(HttpMethod.Post, FilesHandlers.UploadPath) { Content = form };
            upload.Headers.Add("X-Auth-Token", live.XAuthToken);
            Assert.Equal(HttpStatusCode.OK, (await proxy.Client.SendAsync(upload, Ct)).StatusCode);

            // An edit: the details sealed again with the same key and a new IV.
            const string edited =
                """{"metadata":{"title":"lib.jpg","fileType":0,"creationTime":1},"magicMetadata":{"version":2,"count":1,"data":{"visibility":1}}}""";
            var editKey = key with { Iv = RandomNumberGenerator.GetBytes(16) };
            await Json(HttpMethod.Put, "/files/magic-metadata", new JsonObject
            {
                ["metadataList"] = new JsonArray(new JsonObject { ["id"] = fileId, ["homebase"] = live.Seal(edited, editKey) }),
            });
            var afterEdit = (await InAlbum(first, fileId))!;
            Assert.Equal(edited, live.Open(afterEdit["homebase"]!));
            var stored = await proxy.Client.SendAsync(
                Request(HttpMethod.Get, $"{FilesHandlers.UploadPath}/{fileId}/original"), Ct);
            Assert.Equal(original,
                OdinCrypto.AesCbcDecrypt(key.AesKey, payloadIv, await stored.Content.ReadAsByteArrayAsync(Ct)));

            // A rename.
            await Json(HttpMethod.Post, "/collections/rename", new JsonObject
            {
                ["collectionID"] = first,
                ["homebase"] = live.Seal("""{"type":"album","name":"renamed"}""", LiveHomebase.FileKey.New()),
            });
            var renamed = await Json(HttpMethod.Get, $"/collections/{first}");
            Assert.Equal("""{"type":"album","name":"renamed"}""", live.Open(renamed["collection"]!["homebase"]!));

            // Added to the second album, then removed from it.
            await Json(HttpMethod.Post, "/collections/add-files",
                new JsonObject { ["collectionID"] = second, ["files"] = new JsonArray(Files(fileId)) });
            Assert.False((await InAlbum(second, fileId))!["isDeleted"]!.GetValue<bool>());
            await Json(HttpMethod.Post, "/collections/v3/remove-files",
                new JsonObject { ["collectionID"] = second, ["fileIDs"] = new JsonArray(fileId) });
            Assert.True((await InAlbum(second, fileId))!["isDeleted"]!.GetValue<bool>());

            // Moved from the first album to the second.
            await Json(HttpMethod.Post, "/collections/move-files", new JsonObject
            {
                ["fromCollectionID"] = first,
                ["toCollectionID"] = second,
                ["files"] = new JsonArray(Files(fileId)),
            });
            Assert.True((await InAlbum(first, fileId))!["isDeleted"]!.GetValue<bool>());
            Assert.False((await InAlbum(second, fileId))!["isDeleted"]!.GetValue<bool>());

            // Trashed: out of the album, in the trash for 30 days.
            await Json(HttpMethod.Post, "/files/trash", new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["fileID"] = fileId, ["collectionID"] = second }),
            });
            Assert.True((await InAlbum(second, fileId))!["isDeleted"]!.GetValue<bool>());
            var trashed = (await InTrash(fileId))!;
            Assert.False(trashed["isDeleted"]!.GetValue<bool>());
            Assert.False(trashed["isRestored"]!.GetValue<bool>());
            Assert.Equal(second, trashed["file"]!["collectionID"]!.GetValue<long>());
            Assert.Equal(edited, live.Open(trashed["file"]!["homebase"]!));
            Assert.Equal(EnteDrive.TrashMicros,
                trashed["deleteBy"]!.GetValue<long>() - trashed["createdAt"]!.GetValue<long>());

            // Restored into the first album, which then can't be deleted keeping its photos.
            await Json(HttpMethod.Post, "/collections/restore-files",
                new JsonObject { ["collectionID"] = first, ["files"] = new JsonArray(Files(fileId)) });
            Assert.False((await InAlbum(first, fileId))!["isDeleted"]!.GetValue<bool>());
            Assert.True((await InTrash(fileId))!["isRestored"]!.GetValue<bool>());
            Assert.Equal(HttpStatusCode.Conflict,
                await Status(HttpMethod.Delete, $"/collections/v3/{first}?collectionID={first}&keepFiles=true"));

            // Trashed again and deleted for good.
            await Json(HttpMethod.Post, "/files/trash", new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["fileID"] = fileId, ["collectionID"] = first }),
            });
            Assert.False((await InTrash(fileId))!["isRestored"]!.GetValue<bool>());
            await Json(HttpMethod.Post, "/trash/delete", new JsonObject { ["fileIDs"] = new JsonArray(fileId) });
            Assert.True((await InTrash(fileId))!["isDeleted"]!.GetValue<bool>());
            using var http = new HttpClient();
            var gone = await new OdinClient(http).GetHeaderByUniqueIdAsync(live.Credentials, EnteDrive.Alias,
                EnteIds.ToGuid(EnteIds.Kind.File, fileId), Ct);
            Assert.Equal(404, gone.Status);

            // Both albums, now empty, deleted: the album list says so.
            Assert.Equal(HttpStatusCode.OK,
                await Status(HttpMethod.Delete, $"/collections/v3/{first}?collectionID={first}&keepFiles=true"));
            Assert.Equal(HttpStatusCode.OK,
                await Status(HttpMethod.Delete, $"/collections/v3/{second}?collectionID={second}&keepFiles=false"));
            var all = (await Json(HttpMethod.Get, "/collections/v2?sinceTime=0"))["collections"]!.AsArray();
            foreach (var album in albums)
            {
                Assert.True(all.Single(c => c!["id"]!.GetValue<long>() == album)!["isDeleted"]!.GetValue<bool>());
            }
        }
        finally
        {
            await live.HardDeleteByUniqueIdAsync(EnteIds.ToGuid(EnteIds.Kind.File, fileId), CancellationToken.None);
            await live.HardDeleteTaggedAsync(
                [EnteIds.ToGuid(EnteIds.Kind.File, fileId), .. albums.Select(a => EnteIds.ToGuid(EnteIds.Kind.Collection, a))],
                CancellationToken.None);
            foreach (var album in albums)
            {
                await live.HardDeleteByUniqueIdAsync(EnteIds.ToGuid(EnteIds.Kind.Collection, album),
                    CancellationToken.None);
            }
        }
    }
}
