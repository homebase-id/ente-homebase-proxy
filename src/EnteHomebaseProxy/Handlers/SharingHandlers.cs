namespace EnteHomebaseProxy.Handlers;

/// <summary>
/// Ente's sharing routes: sharing collections with other Ente users, public
/// links to collections and files, and the public collection API
/// (server/cmd/museum/main.go). All TODOs: Homebase shares between identities
/// in a different way, so none of these is one Odin call.
/// </summary>
public static class SharingHandlers
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // With other Ente users.
        Todo.Map(app, "POST", "/collections/share", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/share/batch", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/share/bulk", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/unshare", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/unshare/bulk", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/leave/{collectionID}", Why.OtherUsers);
        Todo.Map(app, "POST", "/collections/join-link", Why.OtherUsers);
        Todo.Map(app, "GET", "/collections/sharees", Why.OtherUsers);
        Todo.Map(app, "PUT", "/collections/sharee-magic-metadata", Why.OtherUsers);

        // Public links.
        Todo.Map(app, "POST", "/collections/share-url", Why.PublicLinks);
        Todo.Map(app, "PUT", "/collections/share-url", Why.PublicLinks);
        Todo.Map(app, "DELETE", "/collections/share-url/{collectionID}", Why.PublicLinks);
        Todo.Map(app, "POST", "/files/share-url", Why.PublicLinks);
        Todo.Map(app, "GET", "/files/share-url", Why.PublicLinks);
        Todo.Map(app, "PUT", "/files/share-url", Why.PublicLinks);
        Todo.Map(app, "DELETE", "/files/share-url/{fileID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/files/share-urls/", Why.PublicLinks);

        // The public collection API (what a public link's viewer calls).
        Todo.Map(app, "GET", "/public-collection/files/preview/{fileID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/files/thumbnail/v3/{fileID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/files/download/{fileID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/files/download/v3/{fileID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/files/data/fetch", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/files/data/preview", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/diff", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/info", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/upload-url", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/multipart-upload-url", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/file", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/verify-password", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/social/diff", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/comments/diff", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/comments", Why.PublicLinks);
        Todo.Map(app, "PUT", "/public-collection/comments/{commentID}", Why.PublicLinks);
        Todo.Map(app, "DELETE", "/public-collection/comments/{commentID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/reactions/diff", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/reactions", Why.PublicLinks);
        Todo.Map(app, "DELETE", "/public-collection/reactions/{reactionID}", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/participants/masked-emails", Why.PublicLinks);
        Todo.Map(app, "GET", "/public-collection/anon-profiles", Why.PublicLinks);
        Todo.Map(app, "POST", "/public-collection/anon-identity", Why.PublicLinks);
    }
}
