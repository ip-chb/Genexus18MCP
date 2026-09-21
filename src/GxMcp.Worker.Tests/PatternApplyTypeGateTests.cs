using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Item #10 (v2.6.4): apply_pattern parent-type gate. Original bug — applying
    // WWP on a WebPanel created the host but bound it as a Transaction, causing
    // IDE errors. The gate rejects non-eligible types upfront and validates
    // settings.template against the available list before any SDK churn.
    // TryBuildTypeGateRejection is the pure helper; covered here in isolation.
    public class PatternApplyTypeGateTests
    {
        [Theory]
        [InlineData("WorkWithPlus")]
        [InlineData("WWP")]
        [InlineData("workwithplus")]
        [InlineData("wwp")]
        public void Transaction_AnyCase_NoRejection(string key)
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Customer", patternKey: key, parentType: "Transaction",
                callerTemplate: null, availableTemplates: null);
            Assert.Null(r);
        }

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("WebComponent")]
        [InlineData("SDPanel")]
        public void WebPanelKind_NoTemplate_NoRejection(string parentType)
        {
            // Without callerTemplate, the gate doesn't reject — the SDK
            // auto-discovers a template downstream.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel", patternKey: "WorkWithPlus", parentType: parentType,
                callerTemplate: null, availableTemplates: new List<string> { "MatIsoTemplate" });
            Assert.Null(r);
        }

        [Theory]
        [InlineData("Procedure")]
        [InlineData("SDT")]
        [InlineData("Domain")]
        [InlineData("DataProvider")]
        [InlineData("WorkflowDiagram")]
        public void NonEligibleType_Rejected_WithValidParentTypesAndHint(string parentType)
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Foo", patternKey: "WorkWithPlus", parentType: parentType,
                callerTemplate: null, availableTemplates: null);
            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("error", env["status"]!.ToString());
            Assert.Equal(parentType, env["parentType"]!.ToString());
            Assert.Equal("Foo", env["target"]!.ToString());
            Assert.NotNull(env["validParentTypes"]);
            var valid = (JArray)env["validParentTypes"]!;
            Assert.Contains("Transaction", valid);
            Assert.Contains("WebPanel", valid);
            Assert.Contains("WebComponent", valid);
            Assert.Contains("SDPanel", valid);
            Assert.NotNull(env["error"]?["hint"]);
        }

        [Fact]
        public void WebPanel_BadTemplate_RejectedWithAvailableTemplates()
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "NotInCatalog",
                availableTemplates: new List<string> { "MatIsoTemplate", "TransactionResp2", "PopoverEmpty" });

            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("error", env["status"]!.ToString());
            Assert.Contains("NotInCatalog", env["error"]?["message"]!.ToString());
            var available = (JArray)env["availableTemplates"]!;
            Assert.Equal(3, available.Count);
            Assert.Contains("MatIsoTemplate", available);
        }

        [Fact]
        public void WebPanel_TemplateCaseInsensitiveMatch_NoRejection()
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "matisotemplate",
                availableTemplates: new List<string> { "MatIsoTemplate" });
            Assert.Null(r);
        }

        [Fact]
        public void WebPanel_GoodTemplate_NoRejection()
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "MatIsoTemplate",
                availableTemplates: new List<string> { "MatIsoTemplate", "PopoverEmpty" });
            Assert.Null(r);
        }

        [Theory]
        [InlineData("WebPanel", "webpanel-direct-attach")]
        [InlineData("WebComponent", "webcomponent-direct-attach")]
        [InlineData("SDPanel", "sdpanel-direct-attach")]
        [InlineData("Transaction", "transaction-family")]
        [InlineData("Procedure", "unknown")]
        public void BindingMode_ReflectsParentLifecycle(string parentType, string expected)
        {
            Assert.Equal(expected, PatternApplyService.GetWwpBindingMode(parentType));
        }

        [Fact]
        public void NonWwpPattern_NoRejection_RegardlessOfType()
        {
            // The gate only fires for WorkWithPlus / WWP keys. Other patterns
            // pass through with no opinion on parent type.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Foo", patternKey: "SomeFuturePattern", parentType: "Procedure",
                callerTemplate: null, availableTemplates: null);
            Assert.Null(r);
        }

        [Fact]
        public void NullParentType_TreatedAsIneligible_Rejected()
        {
            // Defensive: TypeDescriptor.Name can be null/empty on edge cases —
            // the gate must still produce a clear rejection rather than crash.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Foo", patternKey: "WorkWithPlus", parentType: null,
                callerTemplate: null, availableTemplates: null);
            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("error", env["status"]!.ToString());
        }

        // ── Issue #260: manifest-driven gate for patterns other than WorkWithPlus ──
        private const string EntityServicesManifest =
            "<Pattern Publisher=\"K2B\" Id=\"589d4b49-e3f9-4d49-aaf4-fad023028eb1\" Name=\"K2BEntityServices\" Version=\"13.1.1.15262\">" +
            "<Definition><InstanceName>K2BEntityServices{0}</InstanceName>" +
            "<ParentObjects><ParentObject Type=\"Transaction\"></ParentObject></ParentObjects></Definition></Pattern>";

        private const string MenuManifest =
            "<Pattern Publisher=\"K2B\" Id=\"ce7b18b7-b5b0-4b27-8c21-b77743938ddf\" Name=\"K2BMenu\" Version=\"13.1.1.15262\">" +
            "<Definition><InstanceName>K2BMenu</InstanceName>" +
            "<ParentObjects><ParentObject Type=\"(None)\" /></ParentObjects></Definition></Pattern>";

        [Fact]
        public void Manifest_AcceptedParentType_NoRejection()
        {
            var es = PatternRegistry.ParseManifest(EntityServicesManifest, "es.Pattern");
            Assert.Null(PatternApplyService.TryBuildManifestTypeGateRejection("Customer", "K2BEntityServices", es, "Transaction"));
        }

        [Fact]
        public void Manifest_UndeclaredParentType_RejectedWithManifestParents()
        {
            var es = PatternRegistry.ParseManifest(EntityServicesManifest, "es.Pattern");

            string r = PatternApplyService.TryBuildManifestTypeGateRejection("MyPanel", "K2BEntityServices", es, "WebPanel");

            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("PatternParentTypeMismatch", env["error"]?["code"]?.ToString());
            Assert.Equal(new[] { "Transaction" }, env["validParentTypes"]!.ToObject<string[]>());
            Assert.Contains("K2BEntityServices", env["error"]?["message"]?.ToString());
            Assert.DoesNotContain("WorkWithPlus", r);
        }

        [Fact]
        public void Manifest_Parentless_RejectedEvenForTransaction()
        {
            var menu = PatternRegistry.ParseManifest(MenuManifest, "menu.Pattern");

            string r = PatternApplyService.TryBuildManifestTypeGateRejection("Customer", "K2BMenu", menu, "Transaction");

            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("PatternParentTypeMismatch", env["error"]?["code"]?.ToString());
            Assert.Contains("no parent object", env["error"]?["message"]?.ToString());
            Assert.Empty((JArray)env["validParentTypes"]!);
            Assert.DoesNotContain("WorkWithPlus", r);
        }

        [Fact]
        public void Manifest_WwpAndBareGuid_NotGatedHere()
        {
            var registry = new PatternRegistry(null);
            Assert.True(registry.TryResolve("WWP", out var wwp));
            Assert.True(registry.TryResolve("11111111-2222-3333-4444-555555555555", out var bare));

            Assert.Null(PatternApplyService.TryBuildManifestTypeGateRejection("Foo", "WWP", wwp, "Procedure"));
            Assert.Null(PatternApplyService.TryBuildManifestTypeGateRejection("Foo", bare.Id.ToString(), bare, "Procedure"));
        }

        [Fact]
        public void WebPanel_EmptyAvailableList_NoTemplateCheck()
        {
            // If template discovery returned an empty list (KB hasn't loaded
            // pattern templates yet), don't pre-emptively reject the caller's
            // template — the downstream SDK call will give a better error.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "Anything",
                availableTemplates: new List<string>());
            Assert.Null(r);
        }
    }
}
