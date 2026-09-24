using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Read-only live contract for issue #241. It accepts either the native
    // NEW/CHANGED inventory or the explicit stable NotSupported envelope when
    // the installed SDK does not expose the versioned model view.
    [Trait("Category", "LiveIssue")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class Issue241LiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;

        public Issue241LiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync() => _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task ChangedObjects_UsesFrozenBaselineOrStableNotSupported()
        {
            var listResponse = await _harness.CallToolAsync(
                "genexus_kb_version", new JObject { ["action"] = "list" }, 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(listResponse),
                "version list failed: " + listResponse.ToString(Newtonsoft.Json.Formatting.None));

            var listPayload = LiveGatewayHarness.ParseToolPayload(listResponse);
            Assert.NotNull(listPayload);
            var versions = listPayload!["result"]?["versions"] as JArray;
            Assert.NotNull(versions);

            var frozen = versions!.OfType<JObject>()
                .FirstOrDefault(v => v["isFrozen"]?.Value<bool>() == true);
            if (frozen == null)
            {
                var noBaseline = await _harness.CallToolAsync(
                    "genexus_kb_version", new JObject { ["action"] = "changed_objects" }, 120_000);
                var noBaselinePayload = LiveGatewayHarness.ParseToolPayload(noBaseline);
                Assert.True(LiveGatewayHarness.IsToolError(noBaseline));
                Assert.Equal("NoFrozenVersion",
                    noBaselinePayload?["error"]?["code"]?.ToString()
                    ?? noBaselinePayload?["code"]?.ToString());
                return;
            }

            var response = await _harness.CallToolAsync(
                "genexus_kb_version",
                new JObject
                {
                    ["action"] = "changed_objects",
                    ["fromVersion"] = frozen["name"]?.ToString(),
                    ["offset"] = 0,
                    ["limit"] = 10
                },
                120_000);
            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.NotNull(payload);

            if (LiveGatewayHarness.IsToolError(response))
            {
                Assert.Equal("ChangedObjectsNotSupported", payload!["error"]?["code"]?.ToString());
                return;
            }

            Assert.Equal("ok", payload!["status"]?.ToString());
            Assert.Equal("ChangedObjectsListed", payload["code"]?.ToString());
            var result = payload["result"] as JObject;
            Assert.NotNull(result);
            Assert.NotNull(result!["items"] as JArray);
            Assert.True(result["offset"]?.Value<int>() == 0);
            Assert.True(result["limit"]?.Value<int>() <= 200);
            Assert.NotNull(result["source"]);
        }
    }
}
