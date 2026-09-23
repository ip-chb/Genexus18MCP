using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class Issue58To62RoutingTests
    {
        [Fact]
        public void ApplyPattern_ActionsRoutesToTypedManager()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_apply_pattern", new JObject
            {
                ["name"] = "Customer", ["pattern"] = "WorkWithPlus", ["mode"] = "actions",
                ["action"] = "add_user_action", ["containerName"] = "TableActions",
                ["actionName"] = "BaixarConfiguracao", ["caption"] = "Baixar Configuração"
            })!);
            Assert.Equal("Pattern", routed["module"]?.ToString());
            Assert.Equal("ManageActions", routed["action"]?.ToString());
        }

        [Fact]
        public void Create_ObjectAtomicRoutesWholePayload()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_create", new JObject
            {
                ["action"] = "object_atomic", ["name"] = "P", ["objectType"] = "Procedure",
                ["validate"] = true
            })!);
            Assert.Equal("AtomicCreate", routed["module"]?.ToString());
            Assert.Equal("P", routed["params"]?["name"]?.ToString());
            Assert.Equal("Procedure", routed["params"]?["type"]?.ToString());
            Assert.True(routed["params"]?["validate"]?.Value<bool>() == true);
        }

        [Fact]
        public void Create_OmittedAction_WithSource_RoutesToAtomicCreate()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_create", new JObject
            {
                ["name"] = "P", ["type"] = "Procedure", ["source"] = "msg('hi');"
            })!);
            Assert.Equal("AtomicCreate", routed["module"]?.ToString());
            Assert.Equal("P", routed["params"]?["name"]?.ToString());
            Assert.Equal("msg('hi');", routed["params"]?["source"]?.ToString());
        }

        [Fact]
        public void Create_OmittedAction_WithTypeAndName_RoutesToObjectCreate()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_create", new JObject
            {
                ["name"] = "Customer", ["type"] = "Transaction"
            })!);
            Assert.Equal("Object", routed["module"]?.ToString());
            Assert.Equal("Create", routed["action"]?.ToString());
            Assert.Equal("Customer", routed["target"]?.ToString());
            Assert.Equal("Transaction", routed["type"]?.ToString());
        }

        [Fact]
        public void VariableModify_ForwardsReplacementTypeAliases()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_variable", new JObject
            {
                ["action"] = "modify", ["name"] = "P", ["varName"] = "&Value",
                ["newTypeName"] = JValue.CreateNull(), ["typeName"] = "Character(40)", ["dataType"] = "Numeric"
            })!);

            Assert.Equal("ModifyVariable", routed["action"]?.ToString());
            Assert.Equal(JTokenType.Null, routed["newTypeName"]?.Type);
            Assert.Equal("Character(40)", routed["typeName"]?.ToString());
            Assert.Equal("Numeric", routed["dataType"]?.ToString());
        }

        [Fact]
        public void Db_ReorgPreviewRoutesToNonMutatingImpactService()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_db", new JObject
            { ["action"] = "reorg_preview", ["deep"] = true })!);
            Assert.Equal("ReorgImpact", routed["module"]?.ToString());
            Assert.Equal("reorg_preview", routed["params"]?["action"]?.ToString());
        }
    }
}
