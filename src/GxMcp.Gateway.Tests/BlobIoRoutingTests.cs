using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class BlobIoRoutingTests
    {
        [Fact]
        public void ReadBlob_forwards_blob_options_to_worker()
        {
            var routed = new IoRouter().ConvertToolCall("genexus_io", new JObject
            {
                ["action"] = "read_blob",
                ["name"] = "Logo",
                ["type"] = "File",
                ["part"] = "WikiBlob",
                ["outputPath"] = "C:/tmp/logo.bin",
                ["maxBytes"] = 4096,
                ["overwrite"] = true,
                ["includeBase64"] = true
            });

            var json = JObject.FromObject(routed!);
            Assert.Equal("Object", json["module"]!.ToString());
            Assert.Equal("ReadBlob", json["action"]!.ToString());
            Assert.Equal("Logo", json["target"]!.ToString());
            Assert.Equal("WikiBlob", json["part"]!.ToString());
            Assert.Equal("C:/tmp/logo.bin", json["outputPath"]!.ToString());
            Assert.Equal(4096, json["maxBytes"]!.ToObject<int>());
            Assert.True(json["includeBase64"]!.ToObject<bool>());
            Assert.True(json["overwrite"]!.ToObject<bool>());
        }

        [Fact]
        public void ImportPart_forwards_dryRun_and_forceSave_to_worker()
        {
            var routed = new IoRouter().ConvertToolCall("genexus_io", new JObject
            {
                ["action"] = "import_part",
                ["name"] = "WPMain",
                ["type"] = "WebPanel",
                ["part"] = "Events",
                ["inputPath"] = "C:/tmp/events.txt",
                ["dryRun"] = true,
                ["forceSave"] = true
            });

            var json = JObject.FromObject(routed!);
            Assert.Equal("Object", json["module"]!.ToString());
            Assert.Equal("ImportText", json["action"]!.ToString());
            Assert.Equal("WPMain", json["target"]!.ToString());
            Assert.Equal("Events", json["part"]!.ToString());
            Assert.Equal("C:/tmp/events.txt", json["inputPath"]!.ToString());
            Assert.True(json["dryRun"]!.ToObject<bool>());
            Assert.True(json["forceSave"]!.ToObject<bool>());
        }

        [Fact]
        public void ImportPart_defaults_preview_flags_to_false()
        {
            var routed = new IoRouter().ConvertToolCall("genexus_io", new JObject
            {
                ["action"] = "import_part",
                ["name"] = "WPMain",
                ["inputPath"] = "C:/tmp/events.txt"
            });

            var json = JObject.FromObject(routed!);
            Assert.False(json["dryRun"]!.ToObject<bool>());
            Assert.False(json["forceSave"]!.ToObject<bool>());
        }
    }
}
