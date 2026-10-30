using System.Text.Json.Nodes;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// The proxy and the Ente web app agree on contract/ente-homebase.json
/// (CONTRACT.md): the proxy's constants match it, and, with an Ente checkout,
/// so does the web app's constants.ts.
/// </summary>
public class ContractTests
{
    private static readonly JsonNode Contract =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ente-homebase.json")))!;

    [Fact]
    public void TheProxyUsesTheContractsDriveAndFiles()
    {
        Assert.Equal(EnteDrive.Alias, Contract["drive"]!["alias"]!.GetValue<string>());
        Assert.Equal(Guid.Parse(Contract["keyAttributesFile"]!["uniqueId"]!.GetValue<string>()),
            EnteDrive.KeyAttributesUniqueId);
        Assert.Equal(EnteDrive.KeyAttributesFileType, Contract["keyAttributesFile"]!["fileType"]!.GetValue<int>());
    }

    [Fact]
    public void TheUserIdMatchesTheContractsTestValue()
    {
        var test = Contract["testValues"]!["enteUserID"]!;
        Assert.Equal(test["userID"]!.GetValue<long>(), EnteIds.UserId(test["identity"]!.GetValue<string>()));
    }

    [Fact]
    public void TheProxysOwnFileTypesDontCollideWithTheWebApps()
    {
        int[] proxys =
        [
            EnteDrive.CollectionFileType, EnteDrive.ObjectFileType, EnteDrive.UserEntityKeyFileType,
            EnteDrive.FileRecordFileType, EnteDrive.MembershipFileType,
        ];
        Assert.DoesNotContain(Contract["passwordFile"]!["fileType"]!.GetValue<int>(), proxys);
    }

    /// <summary>Every value in the contract appears in the web app's constants.ts.</summary>
    [Fact]
    public void TheWebAppHasTheSameValues()
    {
        var constants = File.ReadAllText(EnteCheckout.FileOrSkip("web/packages/homebase/constants.ts"));
        var missing = new List<string>();
        foreach (var section in new[] { "app", "drive", "passwordFile", "keyAttributesFile" })
        {
            foreach (var (key, value) in Contract[section]!.AsObject())
            {
                var text = value!.ToJsonString().Trim('"');
                if (!constants.Contains(text)) missing.Add($"{section}.{key} = {text}");
            }
        }

        Assert.True(missing.Count == 0, "Not in the web app's constants.ts: " + string.Join(", ", missing));
    }
}
