using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public sealed class ModuleServiceTests : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "GxMcpModuleTests_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private string WriteOpc(string fileName, string manifestXml)
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, fileName);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("ModuleManifest.mf");
                using (var stream = entry.Open())
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    writer.Write(manifestXml);
            }
            return path;
        }

        private const string SampleManifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<ModulePackage>" +
            "<ID>44fbb6d2-0a48-44e7-98e9-64d0e359861f</ID>" +
            "<Name>SecurityAPICommons</Name>" +
            "<Version>3.10.20.183754</Version>" +
            "<Description>SecurityAPI common objects module</Description>" +
            "<Dependencies>" +
            "<PackagedModuleDependency>" +
            "<Name>GeneXusCryptography</Name>" +
            "<Guid>e58026a8-9276-412d-a45d-63252580557f</Guid>" +
            "<Version>3.10.20.183754</Version>" +
            "<MinimumVersion>1.8.6.138122</MinimumVersion>" +
            "<MaximumVersion />" +
            "</PackagedModuleDependency>" +
            "</Dependencies>" +
            "</ModulePackage>";

        [Fact]
        public void ReadOpcPackage_ReturnsIdentityAndDependencies()
        {
            string path = WriteOpc("Sample_1.0.0.0.opc", SampleManifest);

            ModuleService.OpcPackageIdentity package = ModuleService.ReadOpcPackage(path);

            Assert.Equal("SecurityAPICommons", package.Name);
            Assert.Equal("3.10.20.183754", package.Version);
            Assert.Equal("44fbb6d2-0a48-44e7-98e9-64d0e359861f", package.Id);
            Assert.Equal("Sample_1.0.0.0.opc", package.FileName);
            Assert.True(package.FileBytes > 0);
            var dependency = Assert.Single(package.Dependencies);
            Assert.Equal("GeneXusCryptography", dependency.Name);
            Assert.Equal("1.8.6.138122", dependency.MinimumVersion);
        }

        [Fact]
        public void ReadOpcPackage_MissingFile_ThrowsFileNotFound()
        {
            Assert.Throws<FileNotFoundException>(() =>
                ModuleService.ReadOpcPackage(Path.Combine(_tempDir, "Missing_1.0.0.0.opc")));
        }

        [Fact]
        public void ReadOpcPackage_WrongExtension_ThrowsInvalidData()
        {
            string path = WriteOpc("Sample.zip", SampleManifest);

            Assert.Throws<InvalidDataException>(() => ModuleService.ReadOpcPackage(path));
        }

        [Fact]
        public void ReadOpcPackage_CorruptZip_ThrowsCuratedInvalidData()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, "Corrupt_1.0.0.0.opc");
            File.WriteAllText(path, "this is not a zip package");

            var ex = Assert.Throws<InvalidDataException>(() => ModuleService.ReadOpcPackage(path));
            Assert.Contains("could not be read", ex.Message);
        }

        [Fact]
        public void ReadOpcPackage_MissingManifest_ThrowsInvalidData()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, "Empty_1.0.0.0.opc");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("other.txt");
                using (var stream = entry.Open())
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    writer.Write("not a module");
            }

            Assert.Throws<InvalidDataException>(() => ModuleService.ReadOpcPackage(path));
        }

        [Fact]
        public void ReadOpcPackage_ManifestWithoutName_ThrowsInvalidData()
        {
            string path = WriteOpc("Noname_1.0.0.0.opc", "<ModulePackage><Version>1.0</Version></ModulePackage>");

            Assert.Throws<InvalidDataException>(() => ModuleService.ReadOpcPackage(path));
        }

        [Fact]
        public void DescribeExceptionChain_IncludesTypeChainAndBoundsLength()
        {
            var inner = new InvalidOperationException(new string('x', 2000));
            var outer = new ArgumentNullException("cachePath", inner);

            string description = ModuleService.DescribeExceptionChain(outer);

            Assert.Contains("ArgumentNullException", description);
            Assert.Contains("InvalidOperationException", description);
            Assert.True(description.Length <= 1300);
        }

        [Fact]
        public void Install_DryRunWithoutKb_FailsClosedBeforeSdk()
        {
            string path = WriteOpc("Preview_1.0.0.0.opc", SampleManifest);
            var service = new ModuleService(null, null);

            // No KB is open, so even the read-only preview resolves through
            // the NoKbOpen guard first; the preview contract itself is covered
            // live, while manifest parsing above stays unit-verified.
            JObject response = JObject.Parse(service.Run(new JObject
            {
                ["action"] = "install",
                ["opcFile"] = path,
                ["dryRun"] = true
            }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("NoKbOpen", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Package_action_is_recognized_before_kb_resolution()
        {
            JObject response = JObject.Parse(new ModuleService(null, null).Run(new JObject
            {
                ["action"] = "package",
                ["name"] = "Sample",
                ["outputPath"] = "C:\\temp"
            }));

            Assert.NotEqual("BadAction", response["error"]?["code"]?.ToString());
            Assert.Equal("NoKbOpen", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Unknown_action_returns_a_structured_error()
        {
            JObject response = JObject.Parse(new ModuleService(null, null).Run(new JObject
            {
                ["action"] = "not-a-module-action"
            }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("BadAction", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Stable_module_key_prefers_sdk_identity_and_preserves_homonyms()
        {
            Assert.Equal(
                "guid:abc",
                ModuleService.BuildStableModuleKey("ABC", "entity-1", "One/Shared", "Shared"));
            Assert.Equal(
                "entity:entity-1",
                ModuleService.BuildStableModuleKey(null, "Entity-1", "One/Shared", "Shared"));
            Assert.NotEqual(
                ModuleService.BuildStableModuleKey(null, null, "One/Shared", "Shared"),
                ModuleService.BuildStableModuleKey(null, null, "Two/Shared", "Shared"));
        }
    }
}
