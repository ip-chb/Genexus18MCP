using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class OperationInventoryClassifierParityTests
    {
        [Fact]
        public void PublishedRecoveryContractsMatchOperationClassifierIncludingDefaultsAndVariants()
        {
            string inventoryPath = Path.Combine(FindRepositoryRoot(), "docs", "operation-contract-inventory.json");
            JObject inventory = JObject.Parse(File.ReadAllText(inventoryPath));

            AssertPolicy(inventory, "genexus_connection_recover", "journal_status",
                new JObject { ["action"] = "journal_status" });
            AssertPolicy(inventory, "genexus_connection_recover", "journal_repair",
                new JObject { ["action"] = "journal_repair" });
            AssertPolicy(inventory, "genexus_connection_recover", "journal_repair",
                new JObject { ["action"] = "journal_repair", ["dryRun"] = JValue.CreateNull() });
            AssertPolicy(inventory, "genexus_connection_recover", "journal_repair",
                new JObject { ["action"] = "journal_repair", ["dryRun"] = true });
            AssertPolicy(inventory, "genexus_connection_recover", "journal_repair",
                new JObject { ["action"] = "journal_repair", ["dryRun"] = false });
            AssertPolicy(inventory, "genexus_connection_recover", "recover", new JObject());
            AssertPolicy(inventory, "genexus_worker_reload", null, new JObject());
        }

        private static void AssertPolicy(JObject inventory, string toolName, string? action, JObject args)
        {
            JObject tool = inventory["tools"]!.OfType<JObject>()
                .Single(entry => string.Equals(entry["tool"]?.Value<string>(), toolName, StringComparison.Ordinal));
            JObject row = tool["actions"]!.OfType<JObject>()
                .Single(entry => action == null
                    ? entry["action"]?.Type == JTokenType.Null
                    : string.Equals(entry["action"]?.Value<string>(), action, StringComparison.Ordinal));

            if (string.Equals(action, "journal_repair", StringComparison.Ordinal))
            {
                bool apply = args["dryRun"]?.Type == JTokenType.Boolean
                    && args["dryRun"]!.Value<bool>() == false;
                row = row["variants"]!.OfType<JObject>().Single(variant =>
                    apply
                        ? variant["selector"]?["dryRun"]?.Type == JTokenType.Boolean
                            && variant["selector"]!["dryRun"]!.Value<bool>() == false
                        : string.Equals(variant["selector"]?["dryRun"]?.Value<string>(), "not false", StringComparison.Ordinal));
            }

            OperationClassifier.OperationContract contract = OperationClassifier.Describe(toolName, args);
            Assert.Equal(contract.Kind switch
            {
                OperationClassifier.OperationKind.ReadOnly => "readOnly",
                OperationClassifier.OperationKind.Mutating => "mutating",
                _ => "unknown"
            }, row["kind"]?.Value<string>());
            Assert.Equal(contract.Effects, row["effects"]?.Value<string>());
            Assert.Equal(contract.Execution, row["execution"]?.Value<string>());
            Assert.Equal(contract.Retry, row["retry"]?.Value<string>());
            Assert.Equal(contract.Cache, row["cache"]?.Value<string>());
            Assert.Equal(contract.Invalidation.ToArray(), row["invalidation"]?.ToObject<string[]>());
            Assert.Equal(contract.PreviewSupported, row["previewSupported"]?.Value<bool>());
        }

        private static string FindRepositoryRoot()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
                directory != null;
                directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Genexus18MCP.sln")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Could not find the Genexus18MCP repository root.");
        }
    }
}
