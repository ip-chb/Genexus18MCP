using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #281: genexus_variable must carry an explicit attribute binding
    // (basedOnAttribute / Attribute:<name>) from the MCP surface to the worker.
    public class VariableAttributeRouterTests
    {
        [Fact]
        public void VariableAdd_ForwardsBasedOnAttribute()
        {
            var message = new OperationsRouter().ConvertToolCall("genexus_variable", JObject.Parse(@"{
                'action':'add',
                'name':'MyProc',
                'varName':'&cttcar',
                'basedOnAttribute':'CttCar'
            }"));

            var routed = JObject.FromObject(message!);
            Assert.Equal("Write", routed["module"]?.ToString());
            Assert.Equal("AddVariable", routed["action"]?.ToString());
            Assert.Equal("CttCar", routed["basedOnAttribute"]?.ToString());
        }

        [Fact]
        public void VariableModify_ForwardsBasedOnAttribute()
        {
            var message = new OperationsRouter().ConvertToolCall("genexus_variable", JObject.Parse(@"{
                'action':'modify',
                'name':'MyProc',
                'varName':'&cttcar',
                'newTypeName':'Attribute:CttCar',
                'basedOnAttribute':'Attribute:CttCar'
            }"));

            var routed = JObject.FromObject(message!);
            Assert.Equal("ModifyVariable", routed["action"]?.ToString());
            Assert.Equal("Attribute:CttCar", routed["basedOnAttribute"]?.ToString());
        }

        [Fact]
        public void SchemaExposesAttributeBindingFields()
        {
            var definitions = JArray.Parse(System.IO.File.ReadAllText(FindToolDefinitions()));
            var variable = definitions.First(x => x["name"]?.ToString() == "genexus_variable");
            Assert.NotNull(variable["inputSchema"]?["properties"]?["basedOnAttribute"]);
            Assert.NotNull(variable["inputSchema"]?["properties"]?["variables"]?["items"]?["properties"]?["basedOnAttribute"]);
        }

        private static string FindToolDefinitions()
        {
            var directory = System.AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = System.IO.Path.Combine(directory, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (System.IO.File.Exists(candidate)) return candidate;
                var parent = System.IO.Directory.GetParent(directory);
                if (parent == null) break;
                directory = parent.FullName;
            }
            throw new System.IO.FileNotFoundException("tool_definitions.json was not found.");
        }
    }
}
