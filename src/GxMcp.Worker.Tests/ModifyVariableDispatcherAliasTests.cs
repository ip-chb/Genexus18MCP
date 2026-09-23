using System;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class ModifyVariableDispatcherAliasTests
    {
        [Fact]
        public void Dispatcher_ModifyVariableAcceptsNewTypeNameAlias()
        {
            var request = new JObject
            {
                ["method"] = "write",
                ["action"] = "ModifyVariable",
                ["target"] = "NonExistentObj_" + Guid.NewGuid().ToString("N"),
                ["params"] = new JObject
                {
                    ["varName"] = "X",
                    ["newTypeName"] = "Character(40)"
                }
            };

            string json = CommandDispatcher.Instance.Dispatch(request.ToString());

            var obj = JObject.Parse(json);
            Assert.NotEqual("UnknownType", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Dispatcher_ModifyVariableAcceptsTypeNameAlias()
        {
            var request = new JObject
            {
                ["method"] = "write",
                ["action"] = "ModifyVariable",
                ["target"] = "NonExistentObj_" + Guid.NewGuid().ToString("N"),
                ["params"] = new JObject
                {
                    ["varName"] = "X",
                    ["newTypeName"] = JValue.CreateNull(),
                    ["typeName"] = "Character(40)"
                }
            };

            string json = CommandDispatcher.Instance.Dispatch(request.ToString());

            var obj = JObject.Parse(json);
            Assert.NotEqual("UnknownType", obj["error"]?["code"]?.ToString());
        }
    }
}
