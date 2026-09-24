using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Focused live regression for the complete Events parent-object save
    // contract. Keep this separate from the broad LiveE2E category so a slow
    // optional scenario cannot hide the result of this release-critical smoke.
    [Trait("Category", "LiveEvents")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class EventsObjectSaveLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;
        private bool _initialized;

        public EventsObjectSaveLiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return;
            await _harness.InitializeAsync();
            _initialized = true;
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task EventsPatch_requires_complete_object_save_and_rejects_stale_version()
        {
            var whoami = await _harness.CallToolAsync("genexus_whoami", new JObject());
            Assert.False(
                LiveGatewayHarness.IsToolError(whoami),
                "genexus_whoami failed: " + whoami.ToString(Newtonsoft.Json.Formatting.None));
            var whoamiPayload = RequirePayload(whoami, "genexus_whoami");
            var geneXus = whoamiPayload["geneXus"] as JObject
                ?? whoamiPayload["result"]?["geneXus"] as JObject;
            Assert.True(geneXus?["versionMatches"]?.ToObject<bool?>() == true,
                "whoami must confirm the selected SDK/KB major: " + whoamiPayload.ToString(Newtonsoft.Json.Formatting.None));
            Assert.False(string.IsNullOrWhiteSpace(geneXus?["matchedMajor"]?.ToString()));
            Assert.NotNull(geneXus?["supportedMajors"]);

            string objectName = "McpEvt" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string marker = "// GXMCP live Events save " + Guid.NewGuid().ToString("N");
            string staleMarker = "// GXMCP stale marker " + Guid.NewGuid().ToString("N");
            bool created = false;
            bool primaryFailed = false;
            try
            {
                var create = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "WebPanel",
                    ["name"] = objectName
                });
                Assert.False(
                    LiveGatewayHarness.IsToolError(create),
                    "genexus_create WebPanel failed: " + create.ToString(Newtonsoft.Json.Formatting.None));
                created = true;

                var initialRead = await _harness.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = objectName,
                    ["part"] = "Events",
                    ["type"] = "WebPanel"
                });
                Assert.False(
                    LiveGatewayHarness.IsToolError(initialRead),
                    "initial Events read failed: " + initialRead.ToString(Newtonsoft.Json.Formatting.None));
                var initialPayload = RequirePayload(initialRead, "initial Events read");
                string initialToken = RequiredField(initialPayload, "versionToken", "initial Events read");
                Assert.NotNull(GetField(initialPayload, "source"));

                var edit = await _harness.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = objectName,
                    ["part"] = "Events",
                    ["mode"] = "patch",
                    ["operation"] = "Append",
                    ["content"] = marker,
                    ["baseVersion"] = initialToken,
                    ["requireObjectSave"] = true,
                    ["return_post_state"] = false
                });
                Assert.False(
                    LiveGatewayHarness.IsToolError(edit),
                    "complete Events patch failed: " + edit.ToString(Newtonsoft.Json.Formatting.None));
                var editPayload = RequirePayload(edit, "complete Events patch");
                Assert.True(FieldAsBool(editPayload, "requireObjectSave"), EvidenceMessage(editPayload));
                Assert.True(FieldAsBool(editPayload, "partPersisted"), EvidenceMessage(editPayload));
                Assert.True(FieldAsBool(editPayload, "objectSaved"), EvidenceMessage(editPayload));
                Assert.True(FieldAsBool(editPayload, "metadataUpdated"), EvidenceMessage(editPayload));
                Assert.True(FieldAsBool(editPayload, "metadataStampPersisted"), EvidenceMessage(editPayload));
                Assert.True(FieldAsBool(editPayload, "otherPartsIntact"), EvidenceMessage(editPayload));
                Assert.Equal("object_save", GetField(editPayload, "persistencePath")?.ToString());
                Assert.NotEqual("ObjectSaveIncomplete", GetField(editPayload, "code")?.ToString());

                var freshRead = await _harness.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = objectName,
                    ["part"] = "Events",
                    ["type"] = "WebPanel"
                });
                Assert.False(
                    LiveGatewayHarness.IsToolError(freshRead),
                    "fresh Events read failed: " + freshRead.ToString(Newtonsoft.Json.Formatting.None));
                var freshPayload = RequirePayload(freshRead, "fresh Events read");
                Assert.Contains(marker, GetField(freshPayload, "source")?.ToString() ?? string.Empty);
                string freshToken = RequiredField(freshPayload, "versionToken", "fresh Events read");
                Assert.NotEqual(initialToken, freshToken);

                var stale = await _harness.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = objectName,
                    ["part"] = "Events",
                    ["mode"] = "patch",
                    ["operation"] = "Append",
                    ["content"] = staleMarker,
                    ["baseVersion"] = initialToken,
                    ["requireObjectSave"] = true
                });
                Assert.True(
                    LiveGatewayHarness.IsToolError(stale),
                    "stale Events patch must be rejected: " + stale.ToString(Newtonsoft.Json.Formatting.None));
                var stalePayload = LiveGatewayHarness.ParseToolPayload(stale);
                string? staleCode = GetField(stalePayload, "code")?.ToString();
                Assert.Contains(staleCode, new[] { "StaleObject", "VersionConflict", "VersionCheckUnavailable" });

                var finalRead = await _harness.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = objectName,
                    ["part"] = "Events",
                    ["type"] = "WebPanel"
                });
                Assert.False(
                    LiveGatewayHarness.IsToolError(finalRead),
                    "final Events read failed: " + finalRead.ToString(Newtonsoft.Json.Formatting.None));
                string finalSource = GetField(RequirePayload(finalRead, "final Events read"), "source")?.ToString() ?? string.Empty;
                Assert.Contains(marker, finalSource);
                Assert.DoesNotContain(staleMarker, finalSource);
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
                            ["name"] = objectName,
                            ["confirm"] = true
                        });
                        if (!primaryFailed)
                        {
                            Assert.False(
                                LiveGatewayHarness.IsToolError(delete),
                                "Events smoke cleanup failed: " + delete.ToString(Newtonsoft.Json.Formatting.None));
                        }
                    }
                    catch when (primaryFailed)
                    {
                        // Preserve the primary assertion/transport failure; the
                        // test harness log remains available for cleanup diagnosis.
                    }
                }
            }
        }

        private static JObject RequirePayload(JObject response, string operation)
        {
            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.NotNull(payload);
            return payload!;
        }

        private static JToken? GetField(JObject? payload, string name)
        {
            if (payload == null) return null;
            return payload[name]
                ?? payload["result"]?[name]
                ?? payload["result"]?["result"]?[name];
        }

        private static string RequiredField(JObject payload, string name, string operation)
        {
            string? value = GetField(payload, name)?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(value), operation + " returned no " + name + ".");
            return value!;
        }

        private static bool FieldAsBool(JObject payload, string name)
            => GetField(payload, name)?.ToObject<bool?>() == true;

        private static string EvidenceMessage(JObject payload)
            => "Events complete-save evidence: " + payload.ToString(Newtonsoft.Json.Formatting.None);
    }
}
