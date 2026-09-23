using System.Reflection;
using GxMcp.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class JsonIngressTests
    {
        [Fact]
        public void ParseObject_PreservesIsoTimestampAsExactString()
        {
            const string timestamp = "2026-09-21T11:05:00.000-03:00";
            string raw = "{\"params\":{\"arguments\":{\"since\":\"" + timestamp + "\"}}}";

            MethodInfo parser = typeof(GxMcp.Worker.Program).GetMethod("TryParseCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
            JObject request = (JObject)parser.Invoke(null, new object[] { raw })!;

            JToken since = request["params"]?["arguments"]?["since"];
            Assert.Equal(JTokenType.String, since.Type);
            Assert.Equal(timestamp, since.Value<string>());
            Assert.Contains("\"since\":\"" + timestamp + "\"", request.ToString(Formatting.None));
        }
    }
}
