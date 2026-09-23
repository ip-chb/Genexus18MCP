using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class AnalyzeCallerSitesTests
    {
        private const string CallerSource =
            "ProcB.Call(&a, &b)\n" +
            "&x = ProcB.Udp(&a)\n" +
            "ProcB.Submit(&a)\n" +
            "&url = ProcB.Link(&a)\n" +
            "Module1.ProcB.Call(&a)\n" +
            "call(ProcB, &a)\n" +
            "call('ProcB', &a)\n" +
            "&x = ProcB(&a)\n" +
            "&ProcB.Call()\n" +
            "SomethingProcB.Call()\n" +
            "// ProcB.Call()";

        [Fact]
        public void FindCallerSites_MatchesSupportedFormsAndRejectsNearMatches()
        {
            var index = BuildIndex(includeCallerEdge: true);
            var service = new AnalyzeService(index, null, new CallerGraphService(index), ReadCallerSource);

            JObject response = JObject.Parse(service.FindCallerSites("ProcB"));
            JArray callers = (JArray)response["result"]!["callers"]!;

            Assert.Equal("CallerSitesFound", response["code"]?.ToString());
            Assert.Equal(8, (int)response["result"]!["callSiteCount"]!);
            Assert.Equal(Enumerable.Range(1, 8), callers.Select(site => (int)site["line"]!));
        }

        [Fact]
        public void SdkCallerSourceScan_UsesTheSameCallSiteMatcher()
        {
            var index = BuildIndex(includeCallerEdge: false);
            var service = new AnalyzeService(index, null, new CallerGraphService(index), ReadCallerSource);

            JArray sites = service.ScanSdkCallerSources(new[] { "Caller" }, "ProcB", "Module1.ProcB");

            Assert.Equal(8, sites.Count);
            Assert.Equal("sdk-reference-cross-check", sites[0]["provenance"]?.ToString());
        }

        [Fact]
        public void TextualIndexEnrichment_AddsCalledByForMemberAndLegacyCalls()
        {
            var index = BuildIndex(includeCallerEdge: false);
            var searchIndex = index.GetIndex();
            var caller = searchIndex.Objects.Values.Single(entry => entry.Name == "Caller");
            var enrichment = new IndexCacheService();

            bool changed = enrichment.EnrichCallsFromTextualSources(
                new[] { "ProcB.Call(&a)\ncall('ProcB', &a)" }, caller, searchIndex);

            var target = searchIndex.Objects.Values.Single(entry => entry.Name == "ProcB");
            Assert.True(changed);
            Assert.Contains("ProcB", caller.Calls);
            Assert.Contains("Caller", target.CalledBy);
        }

        [Fact]
        public void TextualIndexEnrichment_DoesNotBindVariableOrNearNameCalls()
        {
            var index = BuildIndex(includeCallerEdge: false);
            var searchIndex = index.GetIndex();
            var caller = searchIndex.Objects.Values.Single(entry => entry.Name == "Caller");
            var enrichment = new IndexCacheService();

            bool changed = enrichment.EnrichCallsFromTextualSources(
                new[] { "&ProcB.Call(&a)\nSomethingProcB.Call(&a)" }, caller, searchIndex);

            var target = searchIndex.Objects.Values.Single(entry => entry.Name == "ProcB");
            Assert.False(changed);
            Assert.Empty(caller.Calls);
            Assert.Empty(target.CalledBy);
        }

        private static IndexCacheService BuildIndex(bool includeCallerEdge)
        {
            var target = new SearchIndex.IndexEntry
            {
                Name = "ProcB",
                Type = "Procedure",
                Module = "Module1",
                CalledBy = includeCallerEdge ? new List<string> { "Caller" } : new List<string>()
            };
            var caller = new SearchIndex.IndexEntry
            {
                Name = "Caller",
                Type = "Procedure",
                Module = "Module1",
                Calls = new List<string>(),
                CalledBy = new List<string>()
            };
            var index = new IndexCacheService();
            index.LoadFromEntries(new[] { target, caller });
            index.MarkIndexComplete(2);
            return index;
        }

        private static string ReadCallerSource(string callerName, string partName)
            => callerName == "Caller" && partName == "Events" ? CallerSource : null;
    }
}
