namespace EnteHomebaseProxy.Tests;

/// <summary>
/// The Ente repo (homebase-id/homebase-ente) these tests compare against, if
/// there's a checkout: ENTE_REPO, else an "ente" folder next to this repo.
/// Tests that need it skip without it.
/// </summary>
public static class EnteCheckout
{
    public const string Variable = "ENTE_REPO";

    public static string? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable(Variable);
        if (!string.IsNullOrEmpty(fromEnv)) return IsEnte(fromEnv) ? fromEnv : null;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var sibling = Path.Combine(dir.FullName, "ente");
            if (IsEnte(sibling)) return sibling;
        }

        return null;
    }

    /// <summary>A file in the Ente checkout, or a skip if there's none.</summary>
    public static string FileOrSkip(string relative)
    {
        var root = Find();
        if (root is null)
        {
            Assert.Skip($"No Ente checkout: set {Variable}, or clone homebase-id/homebase-ente as ../ente");
        }

        return Path.Combine(root, relative);
    }

    private static bool IsEnte(string path) =>
        File.Exists(Path.Combine(path, "server", "cmd", "museum", "main.go")) &&
        File.Exists(Path.Combine(path, "web", "packages", "homebase", "constants.ts"));
}
