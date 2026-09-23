using System;
using System.Linq;
using System.Text;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class MutationResponseBudgetTests
    {
        [Fact]
        public void DefaultEditResponseOmitsFullSourceAndKeepsPatchReceiptWithinTwoKilobytes()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["source"] = string.Join(
                        "\n",
                        Enumerable.Range(0, 1000).Select(index => "persisted line " + index.ToString("D4"))),
                    ["versionToken"] = "next-version",
                    ["persistedHash"] = new string('a', 64),
                    ["persistedSnippet"] = "changed line",
                    ["post_state"] = new JObject { ["diff"] = "@@ -1 +1 @@\n-old\n+new" }
                }
            };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_edit", new JObject(), omitted);

            Assert.Null(result.SelectToken("result.source"));
            Assert.Equal("next-version", result.SelectToken("result.versionToken")?.ToString());
            Assert.NotNull(result.SelectToken("result.persistedHash"));
            Assert.Equal("changed line", result.SelectToken("result.persistedSnippet")?.ToString());
            Assert.NotNull(result.SelectToken("result.post_state.diff"));
            Assert.Contains("result.source", omitted.Select(field => field.ToString()));
            Assert.True(Encoding.UTF8.GetByteCount(result.ToString(Newtonsoft.Json.Formatting.None)) <= 2048);
        }

        [Fact]
        public void LargeSuccessfulPatchIsCompactedToTwoKilobytesAndKeepsItsReceipt()
        {
            const string changedLine = "// issue289-changed-line";
            string snippet = string.Join("\n", Enumerable.Range(0, 21).Select(index =>
                index == 10 ? changedLine : "// context " + index.ToString("D2") + new string('x', 12)));
            var payload = new JObject
            {
                ["status"] = "success",
                ["code"] = "Applied",
                ["target"] = "LargeResponseProbe",
                ["correlationId"] = "12345678-1234-1234-1234-123456789012",
                ["kbAlias"] = "kbteste",
                ["_meta"] = new JObject { ["kbAlias"] = "kbteste", ["worker"] = "fixture" },
                ["result"] = new JObject
                {
                    ["part"] = "Source",
                    ["operation"] = "patch",
                    ["expectedCount"] = 1,
                    ["matchCount"] = 1,
                    ["_meta"] = new JObject { ["source"] = "worker" },
                    ["source"] = new string('s', 24000),
                    ["content"] = new JObject
                    {
                        ["requested"] = new JObject { ["hash"] = new string('a', 64), ["length"] = 24000, ["snippet"] = new string('r', 350) },
                        ["saved"] = new JObject { ["hash"] = new string('b', 64), ["length"] = 24000, ["snippet"] = new string('v', 350) },
                        ["reRead"] = new JObject { ["hash"] = new string('c', 64), ["length"] = 24000, ["snippet"] = new string('w', 350) }
                    },
                    ["snapshot"] = new JObject { ["path"] = "C:/temp/" + new string('p', 250), ["part"] = "Source" },
                    ["postSaveVerification"] = new JObject
                    {
                        ["matches"] = true,
                        ["versionToken"] = "next-version",
                        ["diff"] = new JObject { ["expectedLine"] = new string('e', 120), ["readLine"] = new string('d', 120) }
                    },
                    ["verification"] = new JObject { ["mode"] = "normalized", ["readCompleted"] = true, ["detail"] = new string('z', 220) },
                    ["persistedHash"] = new string('f', 64),
                    ["normalizedPersistedHash"] = new string('f', 64),
                    ["persistedSnippet"] = snippet,
                    ["versionToken"] = "next-version",
                    ["sdkSaveCompleted"] = true,
                    ["saved"] = true,
                    ["saveAttempted"] = true,
                    ["persistedStateKnown"] = true,
                    ["verified"] = true,
                    ["persisted"] = true,
                    ["changed"] = true,
                    ["reReadConfirmed"] = true,
                    ["persistedVerified"] = true,
                    ["post_state"] = new JObject { ["diff"] = "@@ -11 +11 @@\n-old line\n+" + changedLine }
                }
            };
            var args = new JObject { ["mode"] = "patch", ["name"] = "LargeResponseProbe", ["part"] = "Source" };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_edit", args, omitted);

            Assert.Null(result.SelectToken("result.source"));
            Assert.Equal("next-version", result.SelectToken("result.versionToken")?.ToString());
            Assert.Equal(new string('f', 64), result.SelectToken("result.persistedHash")?.ToString());
            Assert.True(result.SelectToken("result.sdkSaveCompleted")?.Value<bool>());
            Assert.True(result.SelectToken("result.verified")?.Value<bool>());
            Assert.Contains(changedLine, result.SelectToken("result.persistedSnippet")?.ToString() ?? string.Empty);
            Assert.Contains(changedLine, result.SelectToken("result.post_state.diff")?.ToString() ?? string.Empty);
            Assert.True(Encoding.UTF8.GetByteCount(result.ToString(Newtonsoft.Json.Formatting.None)) <= 2048,
                "Large patch receipt exceeded the two-kilobyte target: " + result.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Contains("result.content", omitted.Select(field => field.ToString()));
            Assert.Contains("result.snapshot", omitted.Select(field => field.ToString()));

            JObject toolResult = Program.BuildToolResultContent(result, isError: false, "genexus_edit", args, payloadOwned: true);
            Program.AttachOmittedFieldsMetadata(toolResult, omitted);
            string responseText = toolResult["content"]?[0]?["text"]?.ToString() ?? string.Empty;
            JObject responsePayload = JObject.Parse(responseText);
            Assert.True(Encoding.UTF8.GetByteCount(responseText) <= 2048,
                "Final MCP patch text exceeded the two-kilobyte target: " + responseText);
            Assert.Equal("next-version", responsePayload.SelectToken("result.versionToken")?.ToString());
            Assert.Equal(new string('f', 64), responsePayload.SelectToken("result.persistedHash")?.ToString());
            Assert.Contains(changedLine, responsePayload.SelectToken("result.post_state.diff")?.ToString() ?? string.Empty);
            Assert.Contains("result.content", toolResult.SelectToken("_meta.omittedFields")!.Values<string>());
        }

        [Fact]
        public void NoMatchPatchKeepsDiagnosticsAndFollowUpSuggestions()
        {
            var payload = new JObject
            {
                ["status"] = "NoMatch",
                ["code"] = "NoMatch",
                ["result"] = new JObject
                {
                    ["source"] = "unchanged source",
                    ["postSaveVerification"] = new JObject { ["status"] = "Skipped", ["reason"] = "NoMatch" }
                }
            };
            var args = new JObject { ["mode"] = "patch", ["name"] = "LargeResponseProbe", ["part"] = "Source" };
            var omitted = new JArray();

            JObject shaped = Shape(payload, "genexus_edit", args, omitted);
            JObject toolResult = Program.BuildToolResultContent(shaped, isError: false, "genexus_edit", args, payloadOwned: true);
            string responseText = toolResult["content"]?[0]?["text"]?.ToString() ?? string.Empty;
            JObject responsePayload = JObject.Parse(responseText);

            Assert.Equal("unchanged source", responsePayload.SelectToken("result.source")?.ToString());
            Assert.Equal("Skipped", responsePayload.SelectToken("result.postSaveVerification.status")?.ToString());
            Assert.NotNull(responsePayload["next_legal_actions"]);
            Assert.Empty(omitted);
        }

        [Fact]
        public void ExplicitOptInPreservesPersistedSource()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject { ["source"] = new string('S', 40000) }
            };
            var args = new JObject { ["mode"] = "patch", ["name"] = "LargeResponseProbe", ["includePersistedText"] = true };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_edit", args, omitted);

            Assert.Equal(40000, result.SelectToken("result.source")?.ToString().Length);
            Assert.Empty(omitted);

            JObject toolResult = Program.BuildToolResultContent(result, isError: false, "genexus_edit", args, payloadOwned: true);
            string responseText = toolResult["content"]?[0]?["text"]?.ToString() ?? string.Empty;
            Assert.Equal(40000, JObject.Parse(responseText).SelectToken("result.source")?.ToString().Length);
        }

        [Fact]
        public void OversizedMutationDropsPersistedSnippetAndListsOmittedPaths()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["persistedSnippet"] = new string('V', 12000),
                    ["versionToken"] = "next-version"
                }
            };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_edit", new JObject(), omitted);

            Assert.Null(result.SelectToken("result.persistedSnippet"));
            Assert.Equal("next-version", result.SelectToken("result.versionToken")?.ToString());
            Assert.Contains("result.persistedSnippet", omitted.Select(field => field.ToString()));
            Assert.True(result.ToString(Newtonsoft.Json.Formatting.None).Length <= 8192);
        }

        [Fact]
        public void VariableMutationReturnsRequestedChangesAndPersistedCountInsteadOfFullList()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["source"] = "&Existing : Numeric(4)\n&Added : VarChar(20)\n",
                    ["persistedSnippet"] = "&Existing : Numeric(4)\n&Added : VarChar(20)\n",
                    ["persistedHash"] = new string('b', 64)
                }
            };
            var args = new JObject
            {
                ["action"] = "add",
                ["name"] = "Sales.Invoice",
                ["varName"] = "Added",
                ["typeName"] = "VarChar",
                ["length"] = 20
            };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_variable", args, omitted);

            Assert.Null(result.SelectToken("result.source"));
            Assert.Null(result.SelectToken("result.persistedSnippet"));
            Assert.Equal(2, result.SelectToken("result.persistedVariableCount")?.Value<int>());
            Assert.Equal("Added", result.SelectToken("result.changedVariables[0].name")?.ToString());
            Assert.Contains("result.source", omitted.Select(field => field.ToString()));
            Assert.Contains("result.persistedSnippet", omitted.Select(field => field.ToString()));
        }

        [Fact]
        public void ShortSourceMetadataWithoutPersistenceReceiptIsPreserved()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject { ["source"] = "sdk" }
            };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_edit", new JObject(), omitted);

            Assert.Equal("sdk", result.SelectToken("result.source")?.ToString());
            Assert.Empty(omitted);
        }

        [Fact]
        public void ImportPartOmitsPersistedContentByDefault()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["content"] = new string('V', 24000),
                    ["persistedHash"] = new string('c', 64),
                    ["part"] = "Variables"
                }
            };
            var args = new JObject { ["action"] = "import_part" };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_io", args, omitted);

            Assert.Null(result.SelectToken("result.content"));
            Assert.NotNull(result.SelectToken("result.persistedHash"));
            Assert.Equal("Variables", result.SelectToken("result.part")?.ToString());
            Assert.Contains("result.content", omitted.Select(field => field.ToString()));
        }

        [Fact]
        public void MutationDiffIsBoundedAndMarkedWhenTruncated()
        {
            var payload = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["post_state"] = new JObject
                    {
                        ["diff"] = string.Join("\n", Enumerable.Range(0, 90).Select(index => "@@ line " + index + " @@"))
                    }
                }
            };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_edit", new JObject(), omitted);
            string[] diffLines = result.SelectToken("result.post_state.diff")!.ToString().Split('\n');

            Assert.True(diffLines.Length <= 40);
            Assert.True(result.SelectToken("result.post_state.diffTruncated")?.Value<bool>());
        }

        [Fact]
        public void ReadOnlyResultIsNotShaped()
        {
            var payload = new JObject { ["source"] = new string('R', 12000) };
            var omitted = new JArray();

            JObject result = Shape(payload, "genexus_read", new JObject(), omitted);

            Assert.Equal(12000, result["source"]?.ToString().Length);
            Assert.Empty(omitted);
        }

        [Fact]
        public void OmittedFieldsAreMergedIntoMcpMeta()
        {
            var response = new JObject { ["_meta"] = new JObject { ["kbAlias"] = "fixture" } };
            var omitted = new JArray("source", "persistedSnippet");

            Program.AttachOmittedFieldsMetadata(response, omitted);

            Assert.Equal("fixture", response.SelectToken("_meta.kbAlias")?.ToString());
            Assert.True(response.SelectToken("_meta.omittedFields") is JArray, response.ToString());
            Assert.Equal(2, ((JArray)response.SelectToken("_meta.omittedFields")!).Count);
        }

        private static JObject Shape(JToken payload, string toolName, JObject args, JArray omitted)
        {
            return Assert.IsType<JObject>(Program.ShapeMutationResponse(payload, toolName, args, omitted));
        }
    }
}
