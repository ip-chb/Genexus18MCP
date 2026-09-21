using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #260: the pattern registry is discovered from the installation's
    // Packages\Patterns\*\*.Pattern manifests instead of a hardcoded WWP-only map.
    public class PatternRegistryTests
    {
        private static readonly Guid K2BEntityServicesId = new Guid("589d4b49-e3f9-4d49-aaf4-fad023028eb1");
        private static readonly Guid K2BMenuId = new Guid("ce7b18b7-b5b0-4b27-8c21-b77743938ddf");

        private const string EntityServicesManifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Pattern Publisher=\"K2B\" Id=\"589d4b49-e3f9-4d49-aaf4-fad023028eb1\" Name=\"K2BEntityServices\" Version=\"13.1.1.15262\">" +
            "<Definition><InstanceName>K2BEntityServices{0}</InstanceName>" +
            "<ParentObjects><ParentObject Type=\"Transaction\"></ParentObject></ParentObjects></Definition></Pattern>";

        private const string MenuManifest =
            "<Pattern Publisher=\"K2B\" Id=\"ce7b18b7-b5b0-4b27-8c21-b77743938ddf\" Name=\"K2BMenu\" Version=\"13.1.1.15262\">" +
            "<Definition><InstanceName>K2BMenu</InstanceName>" +
            "<ParentObjects xmlns=\"http://schemas.genexus.com/Patterns/Definition/v1.0\"><ParentObject Type=\"(None)\" /></ParentObjects>" +
            "</Definition></Pattern>";

        private static PatternRegistry K2BRegistry() => new PatternRegistry(new[]
        {
            PatternRegistry.ParseManifest(EntityServicesManifest, "es.Pattern"),
            PatternRegistry.ParseManifest(MenuManifest, "menu.Pattern")
        });

        [Fact]
        public void ParseManifest_ReadsIdentityTemplateAndParents()
        {
            var m = PatternRegistry.ParseManifest(EntityServicesManifest, "es.Pattern");

            Assert.Equal(K2BEntityServicesId, m.Id);
            Assert.Equal("K2BEntityServices", m.Name);
            Assert.Equal("K2B", m.Publisher);
            Assert.Equal("13.1.1.15262", m.Version);
            Assert.Equal("K2BEntityServices{0}", m.InstanceNameTemplate);
            Assert.Equal(new[] { "Transaction" }, m.ParentObjectTypes);
            Assert.Equal("K2BEntityServicesCustomer", m.FormatInstanceName("Customer"));
            Assert.True(m.AcceptsParentType("transaction"));
            Assert.False(m.AcceptsParentType("WebPanel"));
            Assert.False(m.IsWorkWithPlus);
        }

        [Fact]
        public void ParseManifest_NamespacedNoneParent_IsParentless()
        {
            var m = PatternRegistry.ParseManifest(MenuManifest, "menu.Pattern");

            Assert.True(m.IsParentless);
            Assert.Empty(m.ParentObjectTypes);
            Assert.Equal("K2BMenu", m.FormatInstanceName("Anything"));
        }

        [Fact]
        public void ParseManifest_WithoutIdOrName_ReturnsNull()
        {
            Assert.Null(PatternRegistry.ParseManifest("<Pattern Name=\"X\" />", "x"));
            Assert.Null(PatternRegistry.ParseManifest("<Pattern Id=\"not-a-guid\" Name=\"X\" />", "x"));
            Assert.Null(PatternRegistry.ParseManifest("<Other Id=\"589d4b49-e3f9-4d49-aaf4-fad023028eb1\" Name=\"X\" />", "x"));
        }

        [Fact]
        public void Registry_AlwaysContainsWorkWithPlusWithAlias()
        {
            var registry = new PatternRegistry(Enumerable.Empty<PatternManifest>());

            Assert.True(registry.TryResolve("WWP", out var byAlias));
            Assert.True(registry.TryResolve("workwithplus", out var byName));
            Assert.Equal(PatternApplyService.WorkWithPlusPatternId, byAlias.Id);
            Assert.Same(byAlias, byName);
            Assert.True(byName.IsWorkWithPlus);
        }

        [Theory]
        [InlineData("K2BEntityServices")]
        [InlineData("k2bentityservices")]
        [InlineData("589d4b49-e3f9-4d49-aaf4-fad023028eb1")]
        [InlineData("589D4B49-E3F9-4D49-AAF4-FAD023028EB1")]
        public void TryResolve_NameOrGuid_ReturnsManifest(string key)
        {
            Assert.True(K2BRegistry().TryResolve(key, out var m));
            Assert.Equal("K2BEntityServices", m.Name);
            Assert.Equal(K2BEntityServicesId, m.Id);
        }

        [Fact]
        public void TryResolve_UnknownName_Fails_UnknownGuid_ResolvesToBareEntry()
        {
            var registry = K2BRegistry();

            Assert.False(registry.TryResolve("NotAPattern", out _));
            Assert.False(registry.TryResolve("  ", out _));

            var unknown = Guid.NewGuid();
            Assert.True(registry.TryResolve(unknown.ToString(), out var bare));
            Assert.Equal(unknown, bare.Id);
            Assert.Null(registry.FindById(unknown));
        }

        [Fact]
        public void MatchInstanceType_ByTypeGuidOrName()
        {
            var registry = K2BRegistry();

            Assert.Equal("K2BEntityServices", registry.MatchInstanceType(null, K2BEntityServicesId)?.Name);
            Assert.Equal("K2BMenu", registry.MatchInstanceType("k2bmenu", Guid.Empty)?.Name);
            Assert.Equal("WorkWithPlus", registry.MatchInstanceType("WorkWithPlus", Guid.Empty)?.Name);
            Assert.Null(registry.MatchInstanceType("Transaction", Guid.NewGuid()));
        }

        [Fact]
        public void Discover_ScansPackageFolders_AndSkipsMalformedManifests()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-pattern-registry-" + Guid.NewGuid().ToString("N"));
            try
            {
                string patterns = Path.Combine(root, "Packages", "Patterns");
                Directory.CreateDirectory(Path.Combine(patterns, "K2BEntityServices"));
                Directory.CreateDirectory(Path.Combine(patterns, "K2BMenu"));
                Directory.CreateDirectory(Path.Combine(patterns, "Broken"));
                File.WriteAllText(Path.Combine(patterns, "K2BEntityServices", "K2BEntityServices.Pattern"), EntityServicesManifest);
                File.WriteAllText(Path.Combine(patterns, "K2BMenu", "K2BMenu.Pattern"), MenuManifest);
                File.WriteAllText(Path.Combine(patterns, "Broken", "Broken.Pattern"), "<Pattern Id=");

                var discovered = PatternRegistry.Discover(root);

                Assert.Equal(new[] { "K2BEntityServices", "K2BMenu" }, discovered.Select(m => m.Name).OrderBy(n => n).ToArray());
                var registry = new PatternRegistry(discovered);
                Assert.Equal(3, registry.All.Count); // + WorkWithPlus
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Discover_MissingInstallation_ReturnsEmpty()
        {
            Assert.Empty(PatternRegistry.Discover(null));
            Assert.Empty(PatternRegistry.Discover(Path.Combine(Path.GetTempPath(), "gxmcp-missing-" + Guid.NewGuid().ToString("N"))));
        }
    }
}
