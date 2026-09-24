using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Sdk;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "LiveE2E")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class OpenIssueLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;

        public OpenIssueLiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync() => _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task CompileCheckDryRun_UsesPublishedTargetAndCallerControls()
        {
            var list = await _harness.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(list),
                "Procedure listing failed: " + _harness.DiagnosticsSummary());

            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            var entries = listPayload?["results"] as JArray ?? listPayload?["items"] as JArray;
            string? name = entries?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(name))
                throw SkipException.ForSkip("The live KB has no Procedure object to exercise compile_check.");
            string procedureName = name!;

            var response = await _harness.CallToolAsync("genexus_lifecycle", new JObject
            {
                ["action"] = "build",
                ["mode"] = "compile_check",
                ["target"] = "Procedure:" + procedureName,
                ["callers"] = false,
                ["callerCap"] = 1,
                ["buildPlanCap"] = 20,
                ["dryRun"] = true
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(response),
                "compile_check dry-run failed: " + _harness.DiagnosticsSummary());

            var payload = LiveGatewayHarness.ParseToolPayload(response);
            var preview = payload?["result"]?["preview"] as JObject
                ?? payload?["preview"] as JObject;
            Assert.NotNull(preview);
            Assert.Equal("CompileCheck", preview!["action"]?.ToString());
            Assert.Equal("none", preview["includeCallees"]?.ToString());
            Assert.False(preview["callers"]?.ToObject<bool>());
            Assert.Equal(1, preview["callerCap"]?.ToObject<int>());
            Assert.Contains("Procedure:" + procedureName,
                preview["wouldBuild"]?.ToObject<string[]>() ?? Array.Empty<string>());
        }

        [LiveKbFact]
        public async Task ObjectDryRunsAndMissingReadsStayBoundedOnSupportedSdk()
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
            foreach (var type in new[] { "Procedure", "SDT" })
            {
                var response = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = type,
                    ["name"] = "McpIssue218" + type + suffix,
                    ["dryRun"] = true
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(response),
                    type + " dry-run failed: " + _harness.DiagnosticsSummary());
                var payload = LiveGatewayHarness.ParseToolPayload(response);
                Assert.Equal("DryRun", payload?["code"]?.ToString());
                Assert.False(payload?["result"]?["persisted"]?.ToObject<bool>() ?? true);
            }

            string missing = "McpIssue218Missing" + suffix;
            var read = await _harness.CallToolAsync("genexus_read", new JObject
            {
                ["name"] = missing
            }, timeoutMs: 120_000);
            var readPayload = LiveGatewayHarness.ParseToolPayload(read);
            Assert.True(LiveGatewayHarness.IsToolError(read));
            Assert.NotEqual("Internal", readPayload?["error"]?["code"]?.ToString());
            Assert.Contains("ObjectNotFound", readPayload?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty);

            var deletePreview = await _harness.CallToolAsync("genexus_delete_object", new JObject
            {
                ["name"] = missing,
                ["dryRun"] = true
            }, timeoutMs: 120_000);
            var deletePayload = LiveGatewayHarness.ParseToolPayload(deletePreview);
            Assert.True(LiveGatewayHarness.IsToolError(deletePreview));
            Assert.Contains("ObjectNotFound", deletePayload?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty);

            var whoami = await _harness.CallToolAsync("genexus_whoami", new JObject());
            string alias = LiveGatewayHarness.ParseToolPayload(whoami)?["kbAlias"]?.ToString() ?? "kbteste";
            var environments = await _harness.CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "list_environments",
                ["kb"] = alias
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(environments),
                "Explicit-KB environment listing failed: " + _harness.DiagnosticsSummary());
        }

        [LiveKbFact]
        public async Task LargeSourcePatchResponsesStaySmallAndAcceptChainedVersions()
        {
            string procedureName = "McpIssue289Source" + Guid.NewGuid().ToString("N").Substring(0, 10);
            const string firstMarker = "// gxmcp-issue289-first-original";
            const string firstReplacement = "// gxmcp-issue289-first-updated";
            const string secondMarker = "// gxmcp-issue289-second-original";
            const string secondReplacement = "// gxmcp-issue289-second-updated";
            const string thirdMarker = "// gxmcp-issue289-third-original";
            const string thirdReplacement = "// gxmcp-issue289-third-updated";
            bool created = false;
            bool primaryFailed = false;

            try
            {
                var create = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "Procedure",
                    ["name"] = procedureName,
                    ["module"] = "General",
                    ["kb"] = "KBTeste"
                }, timeoutMs: 60_000);
                Assert.False(LiveGatewayHarness.IsToolError(create),
                    "Procedure create failed: " + create.ToString(Newtonsoft.Json.Formatting.None));
                created = true;

                string[] sourceLines = Enumerable.Range(0, 1000)
                    .Select(index => "// issue289-padding-" + index.ToString("D4"))
                    .ToArray();
                sourceLines[100] = firstMarker;
                sourceLines[500] = secondMarker;
                sourceLines[900] = thirdMarker;
                var fullWrite = await _harness.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = procedureName,
                    ["part"] = "Source",
                    ["mode"] = "full",
                    ["content"] = string.Join(Environment.NewLine, sourceLines),
                    ["kb"] = "KBTeste"
                }, timeoutMs: 60_000);
                Assert.False(LiveGatewayHarness.IsToolError(fullWrite),
                    "Large Source write failed: " + fullWrite.ToString(Newtonsoft.Json.Formatting.None));
                JObject? fullPayload = LiveGatewayHarness.ParseToolPayload(fullWrite);
                JObject? fullResult = fullPayload?["result"] as JObject ?? fullPayload;
                Assert.NotNull(fullResult);
                Assert.Null(fullResult!["source"]);

                var initialRead = await _harness.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = procedureName,
                    ["part"] = "Source",
                    ["kb"] = "KBTeste"
                }, timeoutMs: 60_000);
                Assert.False(LiveGatewayHarness.IsToolError(initialRead),
                    "Initial Source read failed: " + _harness.DiagnosticsSummary());
                JObject? initialPayload = LiveGatewayHarness.ParseToolPayload(initialRead);
                JObject? initialResult = initialPayload?["result"] as JObject ?? initialPayload;
                string baseVersion = initialResult?["versionToken"]?.ToString() ?? string.Empty;
                Assert.False(string.IsNullOrWhiteSpace(baseVersion), "Initial Source read did not return versionToken.");
                Assert.Contains(firstMarker, initialResult?["source"]?.ToString() ?? string.Empty);

                var firstPatch = await _harness.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = procedureName,
                    ["part"] = "Source",
                    ["mode"] = "patch",
                    ["patch"] = new JObject { ["find"] = firstMarker, ["replace"] = firstReplacement },
                    ["baseVersion"] = baseVersion,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 60_000);
                Assert.False(LiveGatewayHarness.IsToolError(firstPatch),
                    "First versioned patch failed: " + firstPatch.ToString(Newtonsoft.Json.Formatting.None));
                JObject? firstPayload = LiveGatewayHarness.ParseToolPayload(firstPatch);
                JObject? firstResult = firstPayload?["result"] as JObject ?? firstPayload;
                Assert.NotNull(firstResult);
                Assert.Null(firstResult!["source"]);
                Assert.NotNull(firstResult["persistedHash"]);
                Assert.Contains(firstReplacement, firstResult["post_state"]?["diff"]?.ToString() ?? string.Empty);
                string firstPatchText = firstPatch["result"]?["content"]?[0]?["text"]?.ToString() ?? string.Empty;
                int firstPatchBytes = Encoding.UTF8.GetByteCount(firstPatchText);
                string topLevelSizes = string.Join(", ", (firstPayload ?? new JObject()).Properties()
                    .Select(property => property.Name + "=" + Encoding.UTF8.GetByteCount(property.Value.ToString(Newtonsoft.Json.Formatting.None))));
                string resultSizes = string.Join(", ", firstResult.Properties()
                    .Select(property => property.Name + "=" + Encoding.UTF8.GetByteCount(property.Value.ToString(Newtonsoft.Json.Formatting.None))));
                Assert.True(firstPatchBytes <= 2048,
                    $"Default one-line patch text was {firstPatchBytes} bytes; top-level fields: {topLevelSizes}; result fields: {resultSizes}");
                string secondBaseVersion = firstResult["versionToken"]?.ToString() ?? string.Empty;
                Assert.False(string.IsNullOrWhiteSpace(secondBaseVersion), "First patch did not return the next versionToken.");
                Assert.NotEqual(baseVersion, secondBaseVersion);

                var secondPatch = await _harness.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = procedureName,
                    ["part"] = "Source",
                    ["mode"] = "patch",
                    ["patch"] = new JObject { ["find"] = secondMarker, ["replace"] = secondReplacement },
                    ["baseVersion"] = secondBaseVersion,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 60_000);
                Assert.False(LiveGatewayHarness.IsToolError(secondPatch),
                    "Chained versioned patch failed: " + secondPatch.ToString(Newtonsoft.Json.Formatting.None));
                JObject? secondPayload = LiveGatewayHarness.ParseToolPayload(secondPatch);
                JObject? secondResult = secondPayload?["result"] as JObject ?? secondPayload;
                Assert.NotNull(secondResult);
                Assert.Null(secondResult!["source"]);
                string secondPatchText = secondPatch["result"]?["content"]?[0]?["text"]?.ToString() ?? string.Empty;
                int secondPatchBytes = Encoding.UTF8.GetByteCount(secondPatchText);
                Assert.True(secondPatchBytes <= 2048,
                    $"Chained one-line patch text was {secondPatchBytes} bytes: {secondPatchText}");
                string thirdBaseVersion = secondResult["versionToken"]?.ToString() ?? string.Empty;
                Assert.False(string.IsNullOrWhiteSpace(thirdBaseVersion), "Second patch did not return the next versionToken.");
                Assert.NotEqual(secondBaseVersion, thirdBaseVersion);

                var persistedText = await _harness.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = procedureName,
                    ["part"] = "Source",
                    ["mode"] = "patch",
                    ["patch"] = new JObject { ["find"] = thirdMarker, ["replace"] = thirdReplacement },
                    ["baseVersion"] = thirdBaseVersion,
                    ["includePersistedText"] = true,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 60_000);
                Assert.False(LiveGatewayHarness.IsToolError(persistedText),
                    "includePersistedText patch failed: " + _harness.DiagnosticsSummary());
                JObject? persistedPayload = LiveGatewayHarness.ParseToolPayload(persistedText);
                JObject? persistedResult = persistedPayload?["result"] as JObject ?? persistedPayload;
                string fullSource = persistedResult?["source"]?.ToString() ?? string.Empty;
                Assert.True(fullSource.Length > 20_000, "includePersistedText did not restore the complete large Source part.");
                Assert.Contains(firstReplacement, fullSource);
                Assert.Contains(secondReplacement, fullSource);
                Assert.Contains(thirdReplacement, fullSource);
                using var reader = new StringReader(fullSource);
                int lineCount = 0;
                while (reader.ReadLine() != null) lineCount++;
                Assert.Equal(1000, lineCount);
            }
            catch
            {
                primaryFailed = true;
                throw;
            }
            finally
            {
                if (created)
                {
                    try
                    {
                        var delete = await _harness.CallToolAsync("genexus_delete_object", new JObject
                        {
                            ["name"] = procedureName,
                            ["type"] = "Procedure",
                            ["confirm"] = true,
                            ["kb"] = "KBTeste"
                        }, timeoutMs: 60_000);
                        if (!primaryFailed)
                        {
                            Assert.False(LiveGatewayHarness.IsToolError(delete),
                                "Procedure cleanup failed: " + delete.ToString(Newtonsoft.Json.Formatting.None));
                            var verifyCleanup = await _harness.CallToolAsync("genexus_read", new JObject
                            {
                                ["name"] = procedureName,
                                ["kb"] = "KBTeste"
                            }, timeoutMs: 60_000);
                            Assert.True(LiveGatewayHarness.IsToolError(verifyCleanup),
                                "Procedure still exists after cleanup: " + verifyCleanup.ToString(Newtonsoft.Json.Formatting.None));
                        }
                    }
                    catch when (primaryFailed)
                    {
                        // Preserve the primary assertion/transport failure; the harness log remains available.
                    }
                }
            }
        }

        [LiveKbFact]
        public async Task UnknownReadPartReturnsTypedErrorAndResolvableRulesAlias()
        {
            var list = await _harness.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            }, timeoutMs: 60_000);
            Assert.False(LiveGatewayHarness.IsToolError(list),
                "Procedure listing failed: " + _harness.DiagnosticsSummary());

            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            var entries = listPayload?["results"] as JArray ?? listPayload?["items"] as JArray;
            string? name = entries?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(name))
                throw SkipException.ForSkip("The live KB has no Procedure object to exercise PartNotFound.");

            var read = await _harness.CallToolAsync("genexus_read", new JObject
            {
                ["name"] = name,
                ["part"] = "parameters"
            }, timeoutMs: 60_000);
            Assert.True(LiveGatewayHarness.IsToolError(read), "An unknown object part must be reported as an error.");
            var payload = LiveGatewayHarness.ParseToolPayload(read);
            var error = payload?["error"] as JObject ?? payload;
            Assert.Equal("PartNotFound", error?["code"]?.ToString());
            var availableParts = error?["availableParts"] as JArray
                ?? payload?["availableParts"] as JArray;
            Assert.Contains("Rules", availableParts?.Values<string>() ?? Enumerable.Empty<string>());
            Assert.Contains("genexus_inspect", error?["hint"]?.ToString() ?? string.Empty);
        }

        [LiveKbFact]
        public async Task PropertiesBatchGetReturnsOrderedPerTargetResults()
        {
            var list = await _harness.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            }, timeoutMs: 60_000);
            Assert.False(LiveGatewayHarness.IsToolError(list),
                "Procedure listing failed: " + _harness.DiagnosticsSummary());

            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            var entries = listPayload?["results"] as JArray ?? listPayload?["items"] as JArray;
            string? name = entries?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(name))
                throw SkipException.ForSkip("The live KB has no Procedure object to exercise batch properties.");

            string missingName = "McpMissingBatchProperty" + Guid.NewGuid().ToString("N");
            var batch = await _harness.CallToolAsync("genexus_properties", new JObject
            {
                ["action"] = "get",
                ["targets"] = new JArray
                {
                    new JObject { ["name"] = name, ["type"] = "Procedure" },
                    new JObject { ["name"] = missingName, ["type"] = "Procedure" }
                },
                ["projection"] = "minimal"
            }, timeoutMs: 60_000);

            var payload = LiveGatewayHarness.ParseToolPayload(batch);
            var result = payload?["result"] as JObject ?? payload;
            var results = result?["results"] as JArray;
            Assert.NotNull(results);
            Assert.Equal(2, (int?)result?["processedCount"]);
            Assert.Equal(name, results?[0]?["name"]?.ToString());
            Assert.Equal("ok", results?[0]?["status"]?.ToString());
            Assert.Equal(missingName, results?[1]?["name"]?.ToString());
            Assert.Equal("error", results?[1]?["status"]?.ToString());
            Assert.Equal("ObjectNotFound", results?[1]?["error"]?["code"]?.ToString());
        }

        [LiveKbFact]
        public async Task ImportVariables_PreservesModuleQualifiedSdtType_AndModifyDryRunAlias()
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
            string sdtName = "McpIssue298Sdt" + suffix;
            string procedureName = "McpIssue298Proc" + suffix;
            string inputPath = Path.Combine(Path.GetTempPath(), "gxmcp-issue298-" + suffix + ".txt");
            bool sdtCreated = false;
            bool procedureCreated = false;

            try
            {
                var createSdt = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "SDT",
                    ["name"] = sdtName,
                    ["module"] = "General",
                    ["firstItem"] = "Item1",
                    ["firstItemType"] = "Character",
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(createSdt),
                    "SDT create failed: " + createSdt.ToString(Newtonsoft.Json.Formatting.None));
                sdtCreated = true;

                var createProcedure = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "Procedure",
                    ["name"] = procedureName,
                    ["module"] = "General",
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(createProcedure),
                    "Procedure create failed: " + createProcedure.ToString(Newtonsoft.Json.Formatting.None));
                procedureCreated = true;

                var export = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "export_part",
                    ["name"] = procedureName,
                    ["type"] = "Procedure",
                    ["part"] = "Variables",
                    ["outputPath"] = inputPath,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(export),
                    "Variables export failed: " + export.ToString(Newtonsoft.Json.Formatting.None));
                string[] baselineVariables = File.ReadAllLines(inputPath, Encoding.UTF8);
                Assert.NotEmpty(baselineVariables);
                File.WriteAllLines(
                    inputPath,
                    new[] { "&GridState : " + sdtName + ", General", "&PlainLength : Character(25)" }
                        .Concat(baselineVariables),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var import = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "import_part",
                    ["name"] = procedureName,
                    ["part"] = "Variables",
                    ["inputPath"] = inputPath,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(import),
                    "Qualified Variables import failed: " + import.ToString(Newtonsoft.Json.Formatting.None));

                var exportForEdit = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "export_part",
                    ["name"] = procedureName,
                    ["type"] = "Procedure",
                    ["part"] = "Variables",
                    ["outputPath"] = inputPath,
                    ["overwrite"] = true,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(exportForEdit),
                    "Variables re-export failed: " + exportForEdit.ToString(Newtonsoft.Json.Formatting.None));
                string[] editedVariables = File.ReadAllLines(inputPath, Encoding.UTF8);
                int plainLengthIndex = Array.FindIndex(
                    editedVariables,
                    line => line.StartsWith("&PlainLength", StringComparison.OrdinalIgnoreCase));
                Assert.True(plainLengthIndex >= 0, "Exported Variables part lost PlainLength.");
                int typeIndex = editedVariables[plainLengthIndex]
                    .IndexOf("Character(25)", StringComparison.OrdinalIgnoreCase);
                Assert.True(typeIndex >= 0, "Exported Variables part lost PlainLength's original type.");
                string oldType = editedVariables[plainLengthIndex].Substring(typeIndex, "Character(25)".Length);
                string newType = oldType.Substring(0, oldType.IndexOf('(') + 1) + "45)";
                editedVariables[plainLengthIndex] = editedVariables[plainLengthIndex].Substring(0, typeIndex)
                    + newType
                    + editedVariables[plainLengthIndex].Substring(typeIndex + oldType.Length);
                File.WriteAllLines(inputPath, editedVariables, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var reimport = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "import_part",
                    ["name"] = procedureName,
                    ["part"] = "Variables",
                    ["inputPath"] = inputPath,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(reimport),
                    "Variables re-import failed: " + reimport.ToString(Newtonsoft.Json.Formatting.None));

                var readVariables = await _harness.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = procedureName,
                    ["part"] = "Variables",
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(readVariables),
                    "Variables read-back failed: " + readVariables.ToString(Newtonsoft.Json.Formatting.None));
                var variablesPayload = LiveGatewayHarness.ParseToolPayload(readVariables);
                string variablesText = variablesPayload?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;
                Assert.Contains("GridState", variablesText);
                Assert.Contains(sdtName + ", General", variablesText);
                Assert.Contains("PlainLength", variablesText);
                Assert.Contains("Character(45)", variablesText, StringComparison.OrdinalIgnoreCase);

                var modifyPreview = await _harness.CallToolAsync("genexus_variable", new JObject
                {
                    ["action"] = "modify",
                    ["name"] = procedureName,
                    ["varName"] = "GridState",
                    ["typeName"] = "Character(40)",
                    ["dryRun"] = true,
                    ["kb"] = "KBTeste"
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(modifyPreview),
                    "Modify dry-run failed: " + modifyPreview.ToString(Newtonsoft.Json.Formatting.None));
                var modifyPayload = LiveGatewayHarness.ParseToolPayload(modifyPreview);
                var preview = modifyPayload?["result"]?["preview"] as JObject
                    ?? modifyPayload?["preview"] as JObject;
                Assert.NotNull(preview);
                Assert.Equal("Character(40)", preview!["newTypeName"]?.ToString());
                Assert.NotEqual(JTokenType.String, preview["basedOn"]?.Type);
                Assert.NotEqual(JTokenType.String, preview["basedOnAttribute"]?.Type);
            }
            finally
            {
                if (File.Exists(inputPath)) File.Delete(inputPath);

                if (procedureCreated)
                {
                    var deleteProcedure = await _harness.CallToolAsync("genexus_delete_object", new JObject
                    {
                        ["name"] = procedureName,
                        ["type"] = "Procedure",
                        ["confirm"] = true,
                        ["kb"] = "KBTeste"
                    }, timeoutMs: 120_000);
                    Assert.False(LiveGatewayHarness.IsToolError(deleteProcedure),
                        "Procedure cleanup failed: " + deleteProcedure.ToString(Newtonsoft.Json.Formatting.None));
                    var readProcedure = await _harness.CallToolAsync("genexus_read", new JObject
                    {
                        ["name"] = procedureName,
                        ["kb"] = "KBTeste"
                    }, timeoutMs: 120_000);
                    Assert.True(LiveGatewayHarness.IsToolError(readProcedure),
                        "Procedure still exists after cleanup: " + readProcedure.ToString(Newtonsoft.Json.Formatting.None));
                }

                if (sdtCreated)
                {
                    var deleteSdt = await _harness.CallToolAsync("genexus_delete_object", new JObject
                    {
                        ["name"] = sdtName,
                        ["type"] = "SDT",
                        ["confirm"] = true,
                        ["kb"] = "KBTeste"
                    }, timeoutMs: 120_000);
                    Assert.False(LiveGatewayHarness.IsToolError(deleteSdt),
                        "SDT cleanup failed: " + deleteSdt.ToString(Newtonsoft.Json.Formatting.None));
                    var readSdt = await _harness.CallToolAsync("genexus_read", new JObject
                    {
                        ["name"] = sdtName,
                        ["kb"] = "KBTeste"
                    }, timeoutMs: 120_000);
                    Assert.True(LiveGatewayHarness.IsToolError(readSdt),
                        "SDT still exists after cleanup: " + readSdt.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
        }

        [LiveKbFact]
        public async Task DoctorReturnsFreshGatewayTelemetryOnRepeatedCalls()
        {
            var first = LiveGatewayHarness.ParseToolPayload(
                await _harness.CallToolAsync("genexus_doctor", new JObject()));
            await Task.Delay(1100);
            var second = LiveGatewayHarness.ParseToolPayload(
                await _harness.CallToolAsync("genexus_doctor", new JObject()));
            var firstResult = first?["result"] as JObject;
            var secondResult = second?["result"] as JObject;

            Assert.NotNull(firstResult?["checkedAt"]);
            Assert.NotNull(secondResult?["checkedAt"]);
            DateTime firstCheckedAt = firstResult!["checkedAt"]!.Value<DateTime>();
            DateTime secondCheckedAt = secondResult!["checkedAt"]!.Value<DateTime>();
            Assert.True(secondCheckedAt > firstCheckedAt,
                $"Doctor checkedAt did not advance: first={firstCheckedAt:o}, second={secondCheckedAt:o}");
            Assert.Equal("gateway", secondResult["telemetry"]?["source"]?.ToString());
            Assert.True((secondResult["telemetry"]?["totalToolCalls"]?.ToObject<int>() ?? 0) > 0);
        }

        [LiveKbFact]
        public async Task ReadBlob_OverwriteTrue_ReplacesExistingFileAndReportsHash()
        {
            var list = await _harness.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "File",
                ["limit"] = 20
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(list),
                "File listing failed: " + _harness.DiagnosticsSummary());

            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            var entries = listPayload?["results"] as JArray ?? listPayload?["items"] as JArray;
            string? name = entries?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(name))
                throw SkipException.ForSkip("The live KB has no File object to exercise WikiBlob export.");

            string fileName = name!;

            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-read-blob-" + Guid.NewGuid().ToString("N"));
            string outputPath = Path.Combine(tempDir, "blob.bin");
            byte[] sentinel = { 0x73, 0x65, 0x6e, 0x74, 0x69, 0x6e, 0x65, 0x6c };
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllBytes(outputPath, sentinel);

                var response = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "read_blob",
                    ["name"] = fileName,
                    ["type"] = "File",
                    ["part"] = "WikiBlob",
                    ["outputPath"] = outputPath,
                    ["overwrite"] = true
                }, timeoutMs: 120_000);
                var payload = LiveGatewayHarness.ParseToolPayload(response);
                if (LiveGatewayHarness.IsToolError(response))
                {
                    string code = payload?["error"]?["code"]?.ToString()
                        ?? payload?["code"]?.ToString()
                        ?? string.Empty;
                    if (string.Equals(code, "BlobUnavailable", StringComparison.OrdinalIgnoreCase))
                        throw SkipException.ForSkip("The selected File does not expose readable WikiBlob content.");
                    Assert.Fail("read_blob overwrite=true failed: " + (payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "<null>"));
                }

                Assert.NotNull(payload);
                var result = payload!["result"] as JObject ?? payload;
                byte[] written = File.ReadAllBytes(outputPath);
                Assert.False(sentinel.SequenceEqual(written), "overwrite=true left the sentinel file untouched.");
                Assert.Equal(written.LongLength, result["bytes"]?.ToObject<long>());
                Assert.Equal(Sha256(written), result["sha256"]?.ToString());

                string beforeRefusal = Sha256(written);
                var refusal = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "read_blob",
                    ["name"] = fileName,
                    ["type"] = "File",
                    ["part"] = "WikiBlob",
                    ["outputPath"] = outputPath,
                    ["overwrite"] = false
                }, timeoutMs: 120_000);
                var refusalPayload = LiveGatewayHarness.ParseToolPayload(refusal);
                Assert.True(LiveGatewayHarness.IsToolError(refusal),
                    "overwrite=false must reject an existing destination.");
                Assert.Equal("FileAlreadyExists", refusalPayload?["error"]?["code"]?.ToString()
                    ?? refusalPayload?["code"]?.ToString());
                Assert.Equal(beforeRefusal, Sha256(File.ReadAllBytes(outputPath)));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // The harness cleanup remains authoritative; do not hide test evidence.
                }
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(bytes))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
