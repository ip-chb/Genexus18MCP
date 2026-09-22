using System;
using System.Collections.Generic;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class PreviewForwardingTests
    {
        private static readonly IMcpModuleRouter[] Routers = new IMcpModuleRouter[]
        {
            new SystemRouter(), new OperationsRouter()
        };

        // Gateway-owned previews never build a worker command, so there is
        // nothing to forward dryRun through. Keep this list minimal and
        // deliberate: every other DryRunCapable action must carry dryRun=true
        // into the worker envelope (the genexus_io import_part live bug).
        private static readonly HashSet<string> GatewayOwnedPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_connection_recover:journal_repair"
        };

        [Fact]
        public void EveryPreviewCapableAction_ForwardsDryRunToWorker()
        {
            var failures = new List<string>();
            foreach (string key in OperationClassifier.PreviewCapableActions)
            {
                if (GatewayOwnedPreviews.Contains(key)) continue;
                int separator = key.IndexOf(':');
                string tool = key.Substring(0, separator);
                string action = key.Substring(separator + 1);
                var args = new JObject { ["action"] = action, ["dryRun"] = true };
                if (string.Equals(tool, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase))
                    args["mode"] = "actions";
                object? routed = null;
                foreach (var router in Routers)
                {
                    routed = router.ConvertToolCall(tool, args);
                    if (routed != null) break;
                }
                string json = routed == null
                    ? "<null>"
                    : JObject.FromObject(routed).ToString(Formatting.None);
                if (routed == null || json.IndexOf("\"dryRun\":true", StringComparison.Ordinal) < 0)
                    failures.Add(key + " => " + json);
            }
            Assert.True(failures.Count == 0,
                "Preview-capable actions that drop dryRun before the worker:\n" + string.Join("\n", failures));
        }
    }
}
