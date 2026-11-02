using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EnteHomebaseProxy.Handlers;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// An album and a photo, end to end through the running proxy, against a real
/// identity (no mocks). Skipped without HOMEBASE_CREDENTIALS.
///
/// The test plays the web app. It seals the records and encrypts the
/// original and thumbnail with keys only it has, then checks that what comes
/// back opens to exactly what it sent. It deletes the three files it made
/// afterwards, by their uniqueIds, and touches nothing else.
/// </summary>
public class LivePhotoTests(ProxyServer proxy) : IClassFixture<ProxyServer>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Request(HttpMethod method, string path, LiveHomebase live,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-Auth-Token", live.XAuthToken);
        return request;
    }

    private async Task<JsonNode> Json(HttpRequestMessage request)
    {
        var response = await proxy.Client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{request.RequestUri}: {response.StatusCode} {body}");
        return JsonNode.Parse(body)!;
    }

    private async Task<byte[]> Bytes(HttpRequestMessage request)
    {
        var response = await proxy.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    [Fact]
    public async Task APhotoIsStoredAndReadBackAsTheWebAppSealedIt()
    {
        var live = await LiveHomebase.OpenOrSkipAsync(Ct);
        long? collectionId = null;
        var fileId = EnteIds.NewId();
        try
        {
            // The album.
            const string albumJson = """{"type":"album","name":"zero-knowledge test"}""";
            var albumSealed = live.Seal(albumJson, LiveHomebase.FileKey.New());
            var created = await Json(Request(HttpMethod.Post, "/collections", live,
                new StringContent(new JsonObject { ["homebase"] = albumSealed.DeepClone() }.ToJsonString(),
                    Encoding.UTF8, "application/json")));
            collectionId = created["collection"]!["id"]!.GetValue<long>();
            Assert.True(JsonNode.DeepEquals(albumSealed, created["collection"]!["homebase"]));

            var all = await Json(Request(HttpMethod.Get, "/collections/v2?sinceTime=0", live));
            var listed = all["collections"]!.AsArray().Single(c => c!["id"]!.GetValue<long>() == collectionId);
            Assert.Equal(albumJson, live.Open(listed!["homebase"]!));

            // The photo: details sealed with its key, the original and thumbnail
            // encrypted with that key and the payload's own IV.
            var key = LiveHomebase.FileKey.New();
            var payloadIv = RandomNumberGenerator.GetBytes(16);
            var original = RandomNumberGenerator.GetBytes(200_000);
            var thumbnail = RandomNumberGenerator.GetBytes(3_000);
            const string detailsJson = """{"metadata":{"title":"zk.jpg","fileType":0,"creationTime":1}}""";
            var request = new JsonObject
            {
                ["fileID"] = fileId,
                ["collectionID"] = collectionId,
                ["homebase"] = live.Seal(detailsJson, key),
                ["payloadIv"] = Convert.ToBase64String(payloadIv),
                ["contentType"] = "image/jpeg",
                ["thumbnails"] = new JsonArray(new JsonObject
                {
                    ["pixelWidth"] = 320, ["pixelHeight"] = 240, ["contentType"] = "image/webp",
                }),
            };
            var form = new MultipartFormDataContent
            {
                { new StringContent(request.ToJsonString()), "request" },
                {
                    new ByteArrayContent(OdinCrypto.AesCbcEncrypt(key.AesKey, payloadIv, thumbnail))
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/webp") },
                    },
                    "thumbnail", "thumbnail"
                },
                {
                    new ByteArrayContent(OdinCrypto.AesCbcEncrypt(key.AesKey, payloadIv, original))
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                    },
                    "file", "file"
                },
            };
            var uploaded = await Json(Request(HttpMethod.Post, FilesHandlers.UploadPath, live, form));
            Assert.Equal(fileId, uploaded["id"]!.GetValue<long>());

            // The album's changes: the photo, sealed as sent, with its payload IV.
            var diff = await Json(Request(HttpMethod.Get,
                $"/collections/v2/diff?collectionID={collectionId}&sinceTime=0", live));
            var file = diff["diff"]!.AsArray().Single()!;
            Assert.Equal(fileId, file["id"]!.GetValue<long>());
            Assert.Equal(collectionId, file["collectionID"]!.GetValue<long>());
            Assert.Equal($"{EnteDrive.PhotoFormat}:{Convert.ToBase64String(payloadIv)}",
                file["file"]!["decryptionHeader"]!.GetValue<string>());
            Assert.Equal(detailsJson, live.Open(file["homebase"]!));
            Assert.Equal(key.AesKey, live.OpenKey(file["homebase"]!).AesKey);

            // The upload's answer, built from what was sent (no read back), is what Odin reports.
            foreach (var field in new[] { "file", "thumbnail", "info", "metadata", "homebase", "ownerID" })
            {
                Assert.True(JsonNode.DeepEquals(file[field], uploaded[field]),
                    $"{field}: {uploaded[field]?.ToJsonString()} vs {file[field]?.ToJsonString()}");
            }

            // The bytes, passed through: they open with the web app's key.
            var storedOriginal = await Bytes(Request(HttpMethod.Get,
                $"{FilesHandlers.UploadPath}/{fileId}/original", live));
            Assert.Equal(original, OdinCrypto.AesCbcDecrypt(key.AesKey, payloadIv, storedOriginal));
            var storedThumbnail = await Bytes(Request(HttpMethod.Get,
                $"{FilesHandlers.UploadPath}/{fileId}/thumb?width=320&height=240", live));
            Assert.Equal(thumbnail, OdinCrypto.AesCbcDecrypt(key.AesKey, payloadIv, storedThumbnail));

            // On Odin: the photo is zkDrive, its key header as the web app made
            // it, and the membership is unencrypted and holds nothing.
            using var http = new HttpClient();
            var odin = new OdinClient(http);
            var photo = await odin.GetHeaderByUniqueIdAsync(live.Credentials, EnteDrive.Alias,
                EnteIds.ToGuid(EnteIds.Kind.File, fileId), Ct);
            Assert.True(photo.Body!["fileMetadata"]!["isEncrypted"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(request["homebase"]!["keyHeader"],
                SealedRecord.FromHeader(photo.Body!)!.KeyHeader));
            var membership = await odin.GetHeaderByUniqueIdAsync(live.Credentials, EnteDrive.Alias,
                EnteIds.MembershipGuid(collectionId.Value, fileId), Ct);
            Assert.True(membership.IsSuccess, $"membership: HTTP {membership.Status}");
            Assert.False(membership.Body!["fileMetadata"]!["isEncrypted"]!.GetValue<bool>());
            Assert.Null(membership.Body!["fileMetadata"]!["appData"]!["content"]?.GetValue<string>());
        }
        finally
        {
            await live.HardDeleteByUniqueIdAsync(EnteIds.ToGuid(EnteIds.Kind.File, fileId), CancellationToken.None);
            if (collectionId is { } cid)
            {
                await live.HardDeleteByUniqueIdAsync(EnteIds.MembershipGuid(cid, fileId), CancellationToken.None);
                await live.HardDeleteByUniqueIdAsync(EnteIds.ToGuid(EnteIds.Kind.Collection, cid),
                    CancellationToken.None);
            }
        }
    }
}
