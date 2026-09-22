using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // DIR-01: ImportObjectFromText used to accept a legacy "Success" status
    // alongside canonical "ok" when reading WriteService.WriteObject's result.
    // WriteService.WriteObject only ever returns via McpResponse.Ok/Err, so the
    // legacy branch was dead. These tests pin the canonical-only status checks
    // on both the auto-create leg (createJson) and the write leg (writeJson).
    public class ObjectServiceImportTests
    {
        private static ObjectService BuildIsolatedObjectService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            obj.SetWriteService(new WriteService(obj));
            return obj;
        }

        [Fact]
        public void ImportObjectFromText_NoKbOpen_NoTypeFilter_ReturnsObjectNotFound()
        {
            var obj = BuildIsolatedObjectService();
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, "some source");
                string result = obj.ImportObjectFromText("NoSuchObject", tmp);
                var json = JObject.Parse(result);

                Assert.Equal("error", json["status"]?.ToString());
                Assert.Equal("ObjectNotFound", json["error"]?["code"]?.ToString());
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void ImportObjectFromText_NoKbOpen_WithTypeFilter_CreateFailurePropagatesAsIs()
        {
            var obj = BuildIsolatedObjectService();
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, "some source");
                // No KB open: the auto-create leg's CreateObject call cannot succeed.
                // The canonical-only check (status == "ok") must reject whatever
                // shape the failure comes back in and bail out immediately, without
                // ever reaching the write leg.
                string result = obj.ImportObjectFromText("NoSuchObject", tmp, typeFilter: "Procedure");
                var json = JObject.Parse(result);

                Assert.NotEqual("ok", json["status"]?.ToString());
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void ImportObjectFromText_DryRun_MissingObject_PreviewsWithoutCreating()
        {
            var obj = BuildIsolatedObjectService();
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, "some source");
                string result = obj.ImportObjectFromText("NoSuchObject", tmp, dryRun: true);
                var json = JObject.Parse(result);

                Assert.Equal("error", json["status"]?.ToString());
                Assert.Equal("ObjectNotFound", json["error"]?["code"]?.ToString());
                Assert.Contains("dry-run", json["error"]?["message"]?.ToString());
                Assert.False(json["persisted"]?.ToObject<bool?>() ?? true);
            }
            finally { File.Delete(tmp); }
        }
    }
}
