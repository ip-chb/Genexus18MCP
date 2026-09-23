using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Soft-alias guarantee: tools that consolidated into an umbrella (e.g. genexus_browser)
    /// still dispatch when callers use the legacy name. Defaults ON; turned off via
    /// GXMCP_LEGACY_TOOL_ALIASES=0. New umbrella consolidations should add a one-liner here.
    /// </summary>
    public class LegacyToolAliasTests
    {
        private static JObject CallRewrite(string oldName, JObject? args)
        {
            Assert.True(McpRouter.TryRewriteLegacyTool(oldName, args, out var newName, out var newArgs),
                $"Expected '{oldName}' to be rewritten by LegacyToolAliases.");
            var result = new JObject { ["__name"] = newName };
            foreach (var prop in newArgs.Properties())
                result[prop.Name] = prop.Value;
            return result;
        }

        [Fact]
        public void SmokeTest_RewritesToBrowserSmoke()
        {
            var r = CallRewrite("genexus_smoke_test", new JObject { ["target"] = "WPMain" });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("smoke", (string)r["action"]!);
            Assert.Equal("WPMain", (string)r["target"]!);
        }

        [Fact]
        public void A11yAudit_RewritesToBrowserA11y()
        {
            var r = CallRewrite("genexus_a11y_audit", new JObject { ["target"] = "WPMain" });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("a11y", (string)r["action"]!);
        }

        [Fact]
        public void WcagCheck_RewritesToBrowserWcag()
        {
            var r = CallRewrite("genexus_wcag_check", new JObject { ["target"] = "WPMain" });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("wcag", (string)r["action"]!);
        }

        [Fact]
        public void BrowserCapture_RewritesToBrowserCapture()
        {
            var r = CallRewrite("genexus_browser_capture", new JObject { ["target"] = "WPMain", ["capture"] = new JArray("console") });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("capture", (string)r["action"]!);
            Assert.Equal("console", (string)r["capture"]![0]!);
        }

        [Fact]
        public void CrossBrowser_RewritesToBrowserCross()
        {
            var r = CallRewrite("genexus_cross_browser", new JObject { ["target"] = "WPMain", ["browsers"] = new JArray("chrome", "firefox") });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("cross", (string)r["action"]!);
            Assert.Equal(2, ((JArray)r["browsers"]!).Count);
        }

        [Fact]
        public void Preview_RewritesToBrowserPreviewRender_WhenNoSubAction()
        {
            var r = CallRewrite("genexus_preview", new JObject { ["name"] = "WPMain" });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("preview", (string)r["action"]!);
            Assert.Equal("render", (string)r["mode"]!);
        }

        [Fact]
        public void Preview_RewritesToBrowserPreviewRun_WhenSubActionIsRun()
        {
            var r = CallRewrite("genexus_preview", new JObject { ["action"] = "run", ["name"] = "WPMain" });
            Assert.Equal("genexus_browser", (string)r["__name"]!);
            Assert.Equal("preview", (string)r["action"]!);
            Assert.Equal("run", (string)r["mode"]!);
        }

        [Fact]
        public void UnknownTool_NotRewritten()
        {
            Assert.False(McpRouter.TryRewriteLegacyTool("genexus_query", new JObject(), out var newName, out _),
                "Tools that did not consolidate must not be rewritten.");
            Assert.Equal("genexus_query", newName);
        }

        [Fact]
        public void SoftAliasesRemainCallableButAreNotAdvertised()
        {
            string[] aliases =
            {
                "genexus_smoke_test", "genexus_a11y_audit", "genexus_wcag_check", "genexus_browser_capture",
                "genexus_cross_browser", "genexus_preview", "genexus_db_drift", "genexus_db_optimize",
                "genexus_sql", "genexus_generate_sample_data", "genexus_types", "genexus_translations",
                "genexus_history", "genexus_undo", "genexus_time_travel", "genexus_blame", "genexus_diff",
                "genexus_diff_generated", "genexus_asset", "genexus_export_object", "genexus_import_object",
                "genexus_export_unified", "genexus_screenshot_publish", "genexus_ocr_screenshot",
                "genexus_add_variable", "genexus_delete_variable", "genexus_modify_variable",
                "genexus_execution_history", "genexus_watch_event", "genexus_friction_log", "genexus_learning",
                "genexus_logs", "genexus_profile", "genexus_create_object", "genexus_create_popup",
                "genexus_sd_panel", "genexus_save_as", "genexus_forge", "genexus_apply_template"
            };
            var definitions = JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tool_definitions.json")));
            var advertisedNames = definitions.OfType<JObject>()
                .Select(tool => tool["name"]?.ToString() ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string alias in aliases)
            {
                Assert.DoesNotContain(alias, advertisedNames);
                Assert.True(McpRouter.TryRewriteLegacyTool(alias, null, out _, out _),
                    $"Soft alias '{alias}' must remain callable.");
            }
        }

        [Fact]
        public void NullArgs_HandledGracefully()
        {
            Assert.True(McpRouter.TryRewriteLegacyTool("genexus_smoke_test", null, out var newName, out var newArgs));
            Assert.Equal("genexus_browser", newName);
            Assert.Equal("smoke", (string)newArgs["action"]!);
        }
    }
}
