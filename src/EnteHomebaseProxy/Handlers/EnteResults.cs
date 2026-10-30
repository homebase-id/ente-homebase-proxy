using EnteHomebaseProxy.Auth;
using EnteHomebaseProxy.Odin;

namespace EnteHomebaseProxy.Handlers;

/// <summary>Responses in the shapes Ente's server (museum) uses.</summary>
public static class EnteResults
{
    /// <summary>As museum's auth middleware: {"error": "missing token"}.</summary>
    public static IResult MissingToken() => Results.Json(new { error = "missing token" }, statusCode: 401);

    /// <summary>As museum's auth middleware: {"error": "invalid token"}.</summary>
    public static IResult InvalidToken() => Results.Json(new { error = "invalid token" }, statusCode: 401);

    /// <summary>As museum's handler.Error for a plain error status: an empty object.</summary>
    public static IResult Status(int status) => Results.Json(new { }, statusCode: status);

    /// <summary>Ente's ApiError shape: {"code", "message"}.</summary>
    public static IResult ApiError(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    /// <summary>
    /// The credentials in X-Auth-Token, or the 401 to return instead.
    /// </summary>
    public static bool TryGetCredentials(HttpContext context, out HomebaseCredentials credentials,
        out IResult? failure)
    {
        credentials = null!;
        var header = context.Request.Headers[HomebaseCredentials.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            failure = MissingToken();
            return false;
        }

        var parsed = HomebaseCredentials.Parse(header);
        if (parsed is null)
        {
            failure = InvalidToken();
            return false;
        }

        credentials = parsed;
        failure = null;
        return true;
    }

    /// <summary>
    /// An Odin failure as Ente would see it: Odin refusing the credentials is
    /// a 401; anything else is Homebase failing, a 502.
    /// </summary>
    public static IResult OdinFailure(OdinResponse response) =>
        response.Status is 401 or 403
            ? InvalidToken()
            : ApiError(502, "HOMEBASE_ERROR", $"Homebase answered HTTP {response.Status}");
}

/// <summary>
/// An Ente route the proxy doesn't implement yet. Every call to
/// <see cref="Map"/> is a TODO: its reason says which single Odin call the route
/// would map to, or why it can't be one call without state.
/// </summary>
public static class Todo
{
    public static void Map(IEndpointRouteBuilder app, string method, string path, string reason) =>
        app.MapMethods(path, [method], () => EnteResults.ApiError(501, "NOT_IMPLEMENTED", reason))
            .WithMetadata(new TodoReason(reason));

    /// <summary>Endpoint metadata, so tests can list every TODO route and its reason.</summary>
    public sealed record TodoReason(string Reason);
}

/// <summary>Reasons shared by many routes.</summary>
public static class Why
{
    public const string IntIds =
        "Ente file and collection IDs are int64; Odin file IDs are GUIDs. Mapping them needs a lookup " +
        "table (state) or an ID scheme agreed with the client.";

    public const string PresignedUrls =
        "Ente clients upload and download object bytes directly to S3 through presigned URLs. Odin has no " +
        "presigned URLs, so this can't be one Odin call; the client would have to send the bytes through " +
        "the proxy (a different Ente API).";

    public const string Diff =
        "Would be POST /api/v2/drives/{alias}/files/query-batch filtered by fileType, but Ente's sinceTime " +
        "(microseconds) has to become Odin's cursorState, and each Ente record needs its int64 ID. " + IntIds;

    public const string YouAuth = "YouAuth replaces Ente's sign-in; the Ente web app signs in with Homebase itself.";

    public const string Account =
        "Ente account, billing or session management. The identity owns the account on Homebase; there is " +
        "no Odin endpoint for it.";

    public const string OtherUsers =
        "Sharing with other Ente users (by email or user ID). Homebase shares with other identities through " +
        "circles and connections; there's no single Odin call that matches.";

    public const string PublicLinks =
        "Public links. Odin's anonymous drive access is a different model (circles, temporal reads); there's " +
        "no single Odin call that matches.";

    public const string FileData =
        "Derived file data (ML embeddings, video previews). Would be a payload on the file's Odin file, " +
        "but finding that file needs the int64→GUID mapping. " + IntIds;
}
