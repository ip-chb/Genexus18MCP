using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreBenchmarkTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDir;

        public SourceStoreBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-bench-store-" + Guid.NewGuid().ToString("N"));
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
        public void Benchmark_SourceStore_SyntheticCorpus_And_TrigramPruning()
        {
            const int corpusSize = 500;
            var entries = new List<SearchIndex.IndexEntry>(corpusSize);
            var now = DateTime.UtcNow;

            var swBuild = Stopwatch.StartNew();
            for (int i = 0; i < corpusSize; i++)
            {
                string guid = Guid.NewGuid().ToString("D");
                string code = $@"
// Procedure Proc_{i}
For Each Customer
    Where CustomerId = {i}
    CustomerName = 'Customer #{i}'
    Do 'ProcessOrder_{i % 10}'
EndFor
";
                SourceStoreService.Instance.Put(guid, "Source", code, now, "v1");

                entries.Add(new SearchIndex.IndexEntry
                {
                    Guid = guid,
                    Name = $"Proc_{i}",
                    Type = "Procedure",
                    Path = $"\\Procedures\\Proc_{i}",
                    LastUpdate = now
                });
            }
            swBuild.Stop();

            double msPerPut = swBuild.Elapsed.TotalMilliseconds / corpusSize;

            // Measure Trigram Query Latency (p50 / p95)
            var criteria = new SourceSearchCriteria
            {
                Pattern = "ProcessOrder_5",
                MaxResults = 50
            };
            var rx = new Regex("ProcessOrder_5", RegexOptions.IgnoreCase);

            const int queryIterations = 50;
            var latencies = new List<double>(queryIterations);

            // Warmup
            SourceStoreService.Instance.SearchStore(entries, criteria, rx);

            for (int i = 0; i < queryIterations; i++)
            {
                var swQuery = Stopwatch.StartNew();
                var hits = SourceStoreService.Instance.SearchStore(entries, criteria, rx);
                swQuery.Stop();
                latencies.Add(swQuery.Elapsed.TotalMilliseconds);
            }

            latencies.Sort();
            double p50 = latencies[latencies.Count / 2];
            double p95 = latencies[(int)(latencies.Count * 0.95)];

            string report = $@"
=== SOURCE_STORE_BENCHMARK ===
Corpus: {corpusSize} compressed records
Put Throughput: {msPerPut:F2} ms/record
Trigram Search Latency ({queryIterations} runs):
  p50: {p50:F2} ms
  p95: {p95:F2} ms
==============================";

            _output.WriteLine(report);
            Console.WriteLine(report);

            Assert.True(p50 < 200, $"p50 latency should be < 200ms, was {p50:F2}ms");
        }
    }
}
