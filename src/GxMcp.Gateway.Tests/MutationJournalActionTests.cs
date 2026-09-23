using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public class MutationJournalActionTests
    {
        [Fact]
        public async Task JournalStatusIsReachableThroughMcpWithoutSelectingKb()
        {
            var response = await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "journal-status", ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_connection_recover",
                    ["arguments"] = new JObject { ["action"] = "journal_status" }
                }
            }, "journal-no-kb-" + Guid.NewGuid().ToString("N"));
            Assert.Null(response!["error"]);
            var payload = JObject.Parse(response["result"]!["content"]![0]!["text"]!.Value<string>()!);
            Assert.NotNull(payload["healthy"]);
            Assert.NotNull(payload["pendingCount"]);
        }

        [Fact]
        public void RepairDefaultsToPreviewAndNeverStartsAWorker()
        {
            string root = Path.Combine(Path.GetTempPath(), "gx-journal-action-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "journal.json");
                var registry = new MutationRecoveryRegistry(path);
                registry.RequireRead("synthetic", "SyntheticProcedure", "Source", "op");
                string before = File.ReadAllText(path);
                var preview = Program.HandleMutationJournalAction(registry, new JObject { ["action"] = "journal_repair" });
                Assert.True(preview!["dryRun"]!.Value<bool>());
                Assert.False(preview["persisted"]!.Value<bool>());
                Assert.Equal(before, File.ReadAllText(path));
                var applied = Program.HandleMutationJournalAction(registry, new JObject { ["action"] = "journal_repair", ["dryRun"] = false });
                Assert.True(applied!["persisted"]!.Value<bool>());
                Assert.True(applied["verified"]!.Value<bool>());
                Assert.Equal(1, registry.Count);
                Assert.Null(Program.HandleMutationJournalAction(registry, new JObject()));
                Assert.NotNull(Program.HandleMutationJournalAction(registry,
                    new JObject { ["action"] = "journal_repair", ["force"] = true })!["error"]);
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("journal_status", true, "file.read")]
        [InlineData("journal_repair", true, "file.read")]
        [InlineData("journal_repair", false, "file.write")]
        public void JournalActionsAreGatewayOnlyAndUncached(string action, bool dryRun, string effect)
        {
            var args = new JObject { ["action"] = action, ["dryRun"] = dryRun };
            var contract = OperationClassifier.Describe("genexus_connection_recover", args);
            bool preview = action == "journal_status" || dryRun;
            Assert.Equal(preview ? OperationClassifier.OperationKind.ReadOnly : OperationClassifier.OperationKind.Mutating, contract.Kind);
            Assert.Equal(effect, contract.Effects);
            Assert.Equal("gateway", contract.Execution);
            Assert.Equal(preview ? "safe" : "operation_key", contract.Retry);
            Assert.Equal("never", contract.Cache);
            Assert.Equal(preview ? Array.Empty<string>() : new[] { "files" }, contract.Invalidation);
            Assert.Equal(action == "journal_repair", contract.PreviewSupported);
            Assert.False(OperationClassifier.RequiresSessionLease("genexus_connection_recover", args));
        }

        [Fact]
        public void JournalRepairMissingOrNullDryRunIsAReadOnlyPreview()
        {
            var missing = new JObject { ["action"] = "journal_repair" };
            var nullValue = new JObject { ["action"] = "journal_repair", ["dryRun"] = JValue.CreateNull() };

            foreach (JObject args in new[] { missing, nullValue })
            {
                var contract = OperationClassifier.Describe("genexus_connection_recover", args);
                Assert.Equal(OperationClassifier.OperationKind.ReadOnly, contract.Kind);
                Assert.Equal("file.read", contract.Effects);
                Assert.Equal("gateway", contract.Execution);
                Assert.Equal("safe", contract.Retry);
                Assert.Equal("never", contract.Cache);
                Assert.Empty(contract.Invalidation);
                Assert.True(contract.PreviewSupported);
            }
        }

        [Theory]
        [InlineData(false, 0, "token", true)]
        [InlineData(true, 0, "token", false)]
        [InlineData(false, 20, "token", false)]
        [InlineData(false, 0, "", false)]
        public void PartialOrUnversionedReadsCannotClearFence(bool truncated, int offset, string token, bool expected)
            => Assert.Equal(expected, Program.IsCompleteMutationRecoveryRead(new JObject
            { ["truncated"] = truncated, ["offset"] = offset, ["versionToken"] = token }));

        [Fact]
        public void GatewayContentTruncationCannotConfirmButDerivedMetadataTrimmingCan()
        {
            var payload = new JObject { ["versionToken"] = "token", ["isTruncated"] = true };
            Assert.True(Program.IsCompleteMutationRecoveryRead(payload));
            payload["truncatedByGateway"] = true;
            Assert.False(Program.IsCompleteMutationRecoveryRead(payload));
            payload.Remove("truncatedByGateway");
            payload["source"] = new string('x', 2000);
            var (guarded, truncated) = new ResponseSizeGuard(100, _ => { }).Apply(payload, "genexus_read", new JObject());
            Assert.True(truncated);
            Assert.False(Program.IsCompleteMutationRecoveryRead(guarded));
        }
    }
}
