using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public SourceStoreServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-test-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void TrigramExtractor_ExtractTrigrams_ProducesNormalizedTrigrams()
        {
            var trigrams = TrigramExtractor.ExtractTrigrams("Customer");
            Assert.Contains("cus", trigrams);
            Assert.Contains("ust", trigrams);
            Assert.Contains("sto", trigrams);
            Assert.Contains("tom", trigrams);
            Assert.Contains("ome", trigrams);
            Assert.Contains("mer", trigrams);
            Assert.Equal(6, trigrams.Count);

            Assert.Empty(TrigramExtractor.ExtractTrigrams("ab"));
            Assert.Empty(TrigramExtractor.ExtractTrigrams(null));
        }

        [Fact]
        public void TrigramExtractor_ExtractLiteralRuns_HandlesEscapesAndQuantifiers()
        {
            var runs = TrigramExtractor.ExtractLiteralRuns("Customer_Id");
            Assert.Single(runs);
            Assert.Equal("Customer_Id", runs[0]);

            // Optional character quantifier * breaks runs
            var optionalRuns = TrigramExtractor.ExtractLiteralRuns("Cust*omer");
            Assert.Equal(2, optionalRuns.Count);
            Assert.Equal("Cus", optionalRuns[0]);
            Assert.Equal("omer", optionalRuns[1]);

            // Character class is skipped
            var classRuns = TrigramExtractor.ExtractLiteralRuns("Invoice[0-9]+Amount");
            Assert.Equal(2, classRuns.Count);
            Assert.Equal("Invoice", classRuns[0]);
            Assert.Equal("Amount", classRuns[1]);
        }

        [Fact]
        public void TrigramExtractor_ExtractRequiredTrigramSets_AlternationAndWildcard()
        {
            // Simple pattern
            var single = TrigramExtractor.ExtractRequiredTrigramSets("CustomerQuery", null);
            Assert.NotNull(single);
            Assert.Single(single);
            Assert.Contains("cus", single[0]);

            // Alternation
            var alt = TrigramExtractor.ExtractRequiredTrigramSets("Customer|Invoice", null);
            Assert.NotNull(alt);
            Assert.Equal(2, alt.Count);
            Assert.Contains("cus", alt[0]);
            Assert.Contains("inv", alt[1]);

            // Branch with wildcard/no literal runs >= 3 chars returns null (can match anything)
            var wildcard = TrigramExtractor.ExtractRequiredTrigramSets("Customer|.*", null);
            Assert.Null(wildcard);
        }

        [Fact]
        public void TrigramExtractor_IntersectPostings_CorrectlyFiltersKeys()
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["cus"] = new HashSet<string> { "doc1", "doc2" },
                ["ust"] = new HashSet<string> { "doc1", "doc2" },
                ["sto"] = new HashSet<string> { "doc1" },
                ["tom"] = new HashSet<string> { "doc1" },
                ["inv"] = new HashSet<string> { "doc3" },
                ["nvo"] = new HashSet<string> { "doc3" }
            };

            var branches = TrigramExtractor.ExtractRequiredTrigramSets("Custom", null);
            var candidates = TrigramExtractor.IntersectPostings(index, branches, new[] { "doc1", "doc2", "doc3" });

            Assert.Single(candidates);
            Assert.Contains("doc1", candidates);

            // Alternation test
            var altBranches = TrigramExtractor.ExtractRequiredTrigramSets("Custom|Invo", null);
            var altCandidates = TrigramExtractor.IntersectPostings(index, altBranches, new[] { "doc1", "doc2", "doc3" });
            Assert.Equal(2, altCandidates.Count);
            Assert.Contains("doc1", altCandidates);
            Assert.Contains("doc3", altCandidates);
        }

        [Fact]
        public void SourceStoreService_PutAndTryGet_SavesAndRetrievesSource()
        {
            string guid = "11111111-2222-3333-4444-555555555555";
            string part = "Source";
            string code = "Procedure DoSomething\n// Sample comment\n&Total = 100\nEndProc\n";
            var now = DateTime.UtcNow;

            bool putOk = SourceStoreService.Instance.Put(guid, part, code, now, "v1");
            Assert.True(putOk);

            bool getOk = SourceStoreService.Instance.TryGet(guid, part, out string retrieved);
            Assert.True(getOk);
            Assert.Equal(code, retrieved);
        }

        [Fact]
        public void SourceStoreService_CoverageAndFreshness_CalculatedCorrectly()
        {
            string guid1 = "aaaa1111-2222-3333-4444-555555555555";
            string guid2 = "bbbb1111-2222-3333-4444-555555555555";
            var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            SourceStoreService.Instance.Put(guid1, "Source", "code 1", baseTime, "v1");
            SourceStoreService.Instance.Put(guid2, "Source", "code 2", baseTime, "v1");

            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry
                {
                    Guid = guid1,
                    Name = "Proc1",
                    Type = "Procedure",
                    LastUpdate = baseTime // fresh
                },
                new SearchIndex.IndexEntry
                {
                    Guid = guid2,
                    Name = "Proc2",
                    Type = "Procedure",
                    LastUpdate = baseTime.AddMinutes(5) // stale (> baseTime + 2s)
                },
                new SearchIndex.IndexEntry
                {
                    Guid = "cccc1111-2222-3333-4444-555555555555",
                    Name = "Proc3",
                    Type = "Procedure",
                    LastUpdate = baseTime // unindexed / not in store
                }
            };

            var coverage = SourceStoreService.Instance.GetCoverage(entries, null);
            Assert.Equal(3, coverage.TotalObjects);
            Assert.Equal(1, coverage.StoredObjects); // guid1
            Assert.Equal(1, coverage.StaleObjects);  // guid2

            Assert.True(SourceStoreService.Instance.IsStoredAndFresh(entries[0], null));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entries[1], null));
            Assert.False(SourceStoreService.Instance.IsStoredAndFresh(entries[2], null));
        }

        [Fact]
        public void SourceStoreService_SearchStore_FindsPatternWithContext()
        {
            string guid = "dddd1111-2222-3333-4444-555555555555";
            string part = "Source";
            string code = "Line 1: init\nLine 2: setup\nLine 3: target statement\nLine 4: cleanup\nLine 5: return";

            SourceStoreService.Instance.Put(guid, part, code, DateTime.UtcNow, "v1");

            var entry = new SearchIndex.IndexEntry
            {
                Guid = guid,
                Name = "TargetProc",
                Type = "Procedure",
                Path = "\\Root\\TargetProc"
            };

            var criteria = new SourceSearchCriteria
            {
                Pattern = "target statement",
                MaxResults = 10
            };
            var rx = new Regex("target statement", RegexOptions.IgnoreCase);

            var hits = SourceStoreService.Instance.SearchStore(new[] { entry }, criteria, rx);

            Assert.Single(hits);
            var hit = hits[0];
            Assert.Equal("TargetProc", hit["objectName"]?.ToString());
            Assert.Equal(3, (int)hit["line"]);
            Assert.Equal("Line 3: target statement", hit["lineText"]?.ToString());

            var before = hit["contextBefore"] as JArray;
            Assert.NotNull(before);
            Assert.Equal(2, before.Count);
            Assert.Equal("Line 1: init", before[0].ToString());
            Assert.Equal("Line 2: setup", before[1].ToString());

            var after = hit["contextAfter"] as JArray;
            Assert.NotNull(after);
            Assert.Equal(2, after.Count);
            Assert.Equal("Line 4: cleanup", after[0].ToString());
            Assert.Equal("Line 5: return", after[1].ToString());
        }

        [Fact]
        public void SourceStoreService_FlushAndReloadCatalog_PreservesStoredState()
        {
            string guid = "eeee1111-2222-3333-4444-555555555555";
            string part = "Source";
            string code = "for each Customer\n  CustomerName = 'Test'\nendfor";

            SourceStoreService.Instance.Put(guid, part, code, DateTime.UtcNow, "v1");
            SourceStoreService.Instance.FlushCatalog();

            // Reset in-memory state and reload from the same directory
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);

            Assert.Equal(1, SourceStoreService.Instance.StoredRecordCount);
            bool ok = SourceStoreService.Instance.TryGet(guid, part, out string reloaded);
            Assert.True(ok);
            Assert.Equal(code, reloaded);
        }
    }
}
