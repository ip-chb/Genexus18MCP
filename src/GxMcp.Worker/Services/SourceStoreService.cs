using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class SourceStoreCoverage
    {
        public int StoredObjects { get; set; }
        public int StaleObjects { get; set; }
        public int TotalObjects { get; set; }
    }

    public class SourceStoreService
    {
        private static readonly Lazy<SourceStoreService> _instance =
            new Lazy<SourceStoreService>(() => new SourceStoreService());

        public static SourceStoreService Instance => _instance.Value;

        public class RecordSummary
        {
            public string Guid { get; set; }
            public string PartName { get; set; }
            public DateTime? LastUpdate { get; set; }
            public string VersionToken { get; set; }
            public string ContentHash { get; set; }
            public string RelativeFilePath { get; set; }
            public long FileBytes { get; set; }
            public DateTime StoredAtUtc { get; set; }
        }

        private string _storeDirectory;
        private readonly ConcurrentDictionary<string, RecordSummary> _records =
            new ConcurrentDictionary<string, RecordSummary>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, HashSet<string>> _trigramIndex =
            new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly object _ioGate = new object();
        private readonly object _flushGate = new object();
        private readonly object _initGate = new object();
        private Timer _flushTimer;
        private bool _isCatalogDirty;
        private volatile bool _initialized;

        public SourceStoreService()
        {
            _storeDirectory = Path.Combine(RuntimePaths.StateRoot, "source-store");
            Initialize();
        }

        public void SetStoreDirectoryForTest(string dir)
        {
            lock (_ioGate)
            {
                lock (_flushGate)
                {
                    _flushTimer?.Dispose();
                    _flushTimer = null;
                    _isCatalogDirty = false;
                }
                _storeDirectory = dir;
                _records.Clear();
                _trigramIndex.Clear();
                _initialized = false;
                Initialize();
            }
        }

        public string StoreDirectory => _storeDirectory;
        public int StoredRecordCount => _records.Count;

        private void Initialize()
        {
            if (_initialized) return;
            lock (_initGate)
            {
                if (_initialized) return;
                try
                {
                    if (!Directory.Exists(_storeDirectory))
                    {
                        Directory.CreateDirectory(_storeDirectory);
                    }
                    _initialized = true;
                    LoadCatalog();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SOURCE-STORE] Failed to initialize source store at {_storeDirectory}: {ex.Message}");
                }
            }
        }

        private static string MakeKey(string guid, string partName)
        {
            return $"{guid?.Trim().ToLowerInvariant()}:{partName?.Trim().ToLowerInvariant()}";
        }

        public bool Put(string guid, string partName, string source, DateTime? lastUpdate, string versionToken)
        {
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(partName) || source == null)
            {
                return false;
            }

            Initialize();
            guid = guid.Trim().ToLowerInvariant();
            string originalPartName = partName.Trim();
            partName = ObjectService.NormalizeRawSourcePart(partName);
            string key = MakeKey(guid, partName);
            string hash = ComputeHash(source);

            // Check if record exists with identical hash
            if (_records.TryGetValue(key, out var existing))
            {
                if (string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    if (lastUpdate.HasValue && existing.LastUpdate != lastUpdate)
                    {
                        existing.LastUpdate = lastUpdate;
                        existing.VersionToken = versionToken ?? existing.VersionToken;
                        MarkCatalogDirty();
                    }
                    return true;
                }
            }

            try
            {
                string prefix = guid.Length >= 2 ? guid.Substring(0, 2) : "00";
                string subDir = Path.Combine(_storeDirectory, prefix);
                if (!Directory.Exists(subDir)) Directory.CreateDirectory(subDir);

                string fileName = $"{guid}_{partName}.bin.gz";
                string fullPath = Path.Combine(subDir, fileName);
                string relativePath = Path.Combine(prefix, fileName);

                var payload = new JObject
                {
                    ["guid"] = guid,
                    ["part"] = originalPartName,
                    ["lastUpdate"] = lastUpdate?.ToString("o"),
                    ["versionToken"] = versionToken,
                    ["hash"] = hash,
                    ["source"] = source
                };

                byte[] rawBytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
                string tmpPath = Path.Combine(subDir, $"{guid}_{partName}.tmp-{System.Guid.NewGuid():N}");
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                {
                    gz.Write(rawBytes, 0, rawBytes.Length);
                }

                lock (_ioGate)
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    File.Move(tmpPath, fullPath);
                }

                long fileBytes = new FileInfo(fullPath).Length;

                // Update in-memory record summary
                var summary = new RecordSummary
                {
                    Guid = guid,
                    PartName = originalPartName,
                    LastUpdate = lastUpdate,
                    VersionToken = versionToken,
                    ContentHash = hash,
                    RelativeFilePath = relativePath,
                    FileBytes = fileBytes,
                    StoredAtUtc = DateTime.UtcNow
                };

                // Update trigram postings
                var trigrams = TrigramExtractor.ExtractTrigrams(source);
                foreach (var t in trigrams)
                {
                    var set = _trigramIndex.GetOrAdd(t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    lock (set)
                    {
                        set.Add(key);
                    }
                }

                _records[key] = summary;
                MarkCatalogDirty();
                EnforceStorageBudget();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SOURCE-STORE] Error saving source record for {guid} ({partName}): {ex.Message}");
                return false;
            }
        }

        public bool TryGet(string guid, string partName, out string source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(partName)) return false;

            Initialize();
            guid = guid.Trim().ToLowerInvariant();
            partName = ObjectService.NormalizeRawSourcePart(partName);
            string key = MakeKey(guid, partName);

            if (!_records.TryGetValue(key, out var summary) || string.IsNullOrEmpty(summary.RelativeFilePath))
            {
                return false;
            }

            string fullPath = Path.Combine(_storeDirectory, summary.RelativeFilePath);
            if (!File.Exists(fullPath)) return false;

            try
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var payload = JObject.Parse(json);
                    source = payload["source"]?.ToString();
                    return source != null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SOURCE-STORE] Error reading source for {guid} ({partName}): {ex.Message}");
                return false;
            }
        }

        public SourceStoreCoverage GetCoverage(IEnumerable<SearchIndex.IndexEntry> entries, List<string> scope)
        {
            var coverage = new SourceStoreCoverage();
            if (entries == null) return coverage;

            Initialize();
            var entryList = entries as IList<SearchIndex.IndexEntry> ?? entries.ToList();
            coverage.TotalObjects = entryList.Count;

            foreach (var e in entryList)
            {
                if (string.IsNullOrWhiteSpace(e?.Guid)) continue;
                string primaryPart = ObjectService.ResolveSearchPartName(e.Type);
                string key = MakeKey(e.Guid, primaryPart);

                if (_records.TryGetValue(key, out var summary))
                {
                    if (summary.LastUpdate.HasValue && e.LastUpdate > DateTime.MinValue
                        && e.LastUpdate > summary.LastUpdate.Value.AddSeconds(2))
                    {
                        coverage.StaleObjects++;
                    }
                    else
                    {
                        coverage.StoredObjects++;
                    }
                }
            }

            return coverage;
        }

        public bool IsStoredAndFresh(SearchIndex.IndexEntry entry, List<string> scope)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Guid)) return false;
            Initialize();

            string primaryPart = ObjectService.ResolveSearchPartName(entry.Type);
            string key = MakeKey(entry.Guid, primaryPart);

            if (_records.TryGetValue(key, out var summary))
            {
                if (summary.LastUpdate.HasValue && entry.LastUpdate > DateTime.MinValue
                    && entry.LastUpdate > summary.LastUpdate.Value.AddSeconds(2))
                {
                    return false; // Stale
                }
                return true;
            }
            return false;
        }

        public List<JObject> SearchStore(
            IEnumerable<SearchIndex.IndexEntry> storedEntries,
            SourceSearchCriteria criteria,
            Regex rx,
            CancellationToken ct = default(CancellationToken))
        {
            var hits = new List<JObject>();
            if (storedEntries == null) return hits;

            Initialize();
            var entriesByGuid = new Dictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in storedEntries)
            {
                if (!string.IsNullOrWhiteSpace(e.Guid))
                {
                    entriesByGuid[e.Guid.Trim().ToLowerInvariant()] = e;
                }
            }

            if (entriesByGuid.Count == 0) return hits;

            // Extract trigram candidate sets
            var branches = TrigramExtractor.ExtractRequiredTrigramSets(criteria.Pattern, criteria.Callee);
            var candidateKeys = TrigramExtractor.IntersectPostings(_trigramIndex, branches, _records.Keys);

            // Filter candidates to those belonging to the given storedEntries
            var matchedCandidateKeys = new List<string>();
            foreach (var k in candidateKeys)
            {
                int colon = k.IndexOf(':');
                if (colon > 0)
                {
                    string guid = k.Substring(0, colon);
                    if (entriesByGuid.ContainsKey(guid))
                    {
                        matchedCandidateKeys.Add(k);
                    }
                }
            }

            if (matchedCandidateKeys.Count == 0) return hits;

            // Parallel verification off-STA
            var bag = new ConcurrentBag<JObject>();
            Parallel.ForEach(matchedCandidateKeys, new ParallelOptions { CancellationToken = ct }, (key, state) =>
            {
                if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                {
                    state.Stop();
                    return;
                }

                int colon = key.IndexOf(':');
                string guid = key.Substring(0, colon);
                string part = key.Substring(colon + 1);

                if (!entriesByGuid.TryGetValue(guid, out var entry)) return;
                if (!TryGet(guid, part, out string src) || string.IsNullOrEmpty(src)) return;

                string canonicalPart = entry.FullSourcePart;
                if (string.IsNullOrEmpty(canonicalPart))
                {
                    if (_records.TryGetValue(key, out var rec) && !string.IsNullOrEmpty(rec.PartName))
                    {
                        canonicalPart = rec.PartName;
                    }
                }
                if (string.IsNullOrEmpty(canonicalPart))
                {
                    canonicalPart = ObjectService.ResolveSearchPartName(entry.Type, part);
                }
                if (string.Equals(canonicalPart, "events", StringComparison.OrdinalIgnoreCase))
                {
                    canonicalPart = "Events";
                }
                else if (string.Equals(canonicalPart, "source", StringComparison.OrdinalIgnoreCase))
                {
                    canonicalPart = string.Equals(entry.Type, "WebPanel", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
                                 ? "Events" : "Source";
                }

                // Match Callee if requested
                if (!string.IsNullOrEmpty(criteria.Callee))
                {
                    var lines = src.Split('\n');
                    foreach (var call in SourceParser.ParseCalls(src, criteria.IncludeComments))
                    {
                        if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                        {
                            state.Stop();
                            return;
                        }

                        if (!CalleeMatches(call.Callee, criteria.Callee)) continue;
                        if (criteria.ArgMatches != null && !ArgsMatch(call.Args, criteria.ArgMatches)) continue;
                        if (rx != null)
                        {
                            string ln = call.LineNumber - 1 < lines.Length ? lines[call.LineNumber - 1] : "";
                            if (!rx.IsMatch(ln)) continue;
                        }

                        const int ctx = 3;
                        int idx = call.LineNumber - 1;
                        string lineText = idx >= 0 && idx < lines.Length ? lines[idx] : "";
                        var before = new JArray();
                        for (int bi = Math.Max(0, idx - ctx); bi < idx; bi++) before.Add(lines[bi]);
                        var after = new JArray();
                        for (int ai = idx + 1; ai < Math.Min(lines.Length, idx + 1 + ctx); ai++) after.Add(lines[ai]);

                        var hit = new JObject
                        {
                            ["objectName"] = entry.Name,
                            ["type"] = entry.Type,
                            ["guid"] = entry.Guid,
                            ["entityKey"] = entry.EntityKey,
                            ["path"] = entry.Path,
                            ["part"] = canonicalPart,
                            ["callee"] = call.Callee,
                            ["line"] = call.LineNumber,
                            ["lineNumber"] = call.LineNumber,
                            ["lineText"] = lineText,
                            ["contextBefore"] = before,
                            ["contextAfter"] = after,
                            ["args"] = new JArray(call.Args.Select(a => (JToken)a).ToArray())
                        };
                        bag.Add(hit);
                    }
                }
                else if (rx != null)
                {
                    var lines = src.Split('\n');
                    for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                    {
                        if (ct.IsCancellationRequested || bag.Count >= criteria.MaxResults)
                        {
                            state.Stop();
                            return;
                        }

                        string line = lines[lineIdx];
                        if (rx.IsMatch(line))
                        {
                            const int ctx = 3;
                            var before = new JArray();
                            for (int bi = Math.Max(0, lineIdx - ctx); bi < lineIdx; bi++) before.Add(lines[bi]);
                            var after = new JArray();
                            for (int ai = lineIdx + 1; ai < Math.Min(lines.Length, lineIdx + 1 + ctx); ai++) after.Add(lines[ai]);

                            var hit = new JObject
                            {
                                ["objectName"] = entry.Name,
                                ["type"] = entry.Type,
                                ["guid"] = entry.Guid,
                                ["entityKey"] = entry.EntityKey,
                                ["path"] = entry.Path,
                                ["part"] = canonicalPart,
                                ["line"] = lineIdx + 1,
                                ["lineNumber"] = lineIdx + 1,
                                ["lineText"] = line,
                                ["contextBefore"] = before,
                                ["contextAfter"] = after
                            };
                            bag.Add(hit);
                        }
                    }
                }
            });

            hits.AddRange(bag.Take(criteria.MaxResults));
            return hits;
        }

        private static bool CalleeMatches(string actual, string wanted)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(wanted)) return false;
            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = actual.LastIndexOf('.');
            if (dot >= 0)
            {
                return string.Equals(actual.Substring(dot + 1), wanted, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool ArgsMatch(List<string> actualArgs, Dictionary<int, string> expected)
        {
            foreach (var kvp in expected)
            {
                if (kvp.Key < 0 || kvp.Key >= actualArgs.Count) return false;
                if (!string.Equals(actualArgs[kvp.Key]?.Trim(), kvp.Value?.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private void MarkCatalogDirty()
        {
            _isCatalogDirty = true;
            lock (_flushGate)
            {
                if (_flushTimer == null)
                {
                    _flushTimer = new Timer(_ => FlushCatalog(), null, 1500, Timeout.Infinite);
                }
                else
                {
                    _flushTimer.Change(1500, Timeout.Infinite);
                }
            }
        }

        public void FlushCatalog()
        {
            if (!_isCatalogDirty) return;
            lock (_flushGate)
            {
                if (!_isCatalogDirty) return;
                try
                {
                    string catalogPath = Path.Combine(_storeDirectory, "catalog.json.gz");
                    string tmpPath = catalogPath + $".tmp-{Guid.NewGuid():N}";

                    var recordsArray = new JArray();
                    foreach (var kvp in _records)
                    {
                        var rec = kvp.Value;
                        recordsArray.Add(new JObject
                        {
                            ["k"] = kvp.Key,
                            ["g"] = rec.Guid,
                            ["p"] = rec.PartName,
                            ["u"] = rec.LastUpdate?.ToString("o"),
                            ["v"] = rec.VersionToken,
                            ["h"] = rec.ContentHash,
                            ["f"] = rec.RelativeFilePath,
                            ["b"] = rec.FileBytes,
                            ["s"] = rec.StoredAtUtc.ToString("o")
                        });
                    }

                    var root = new JObject
                    {
                        ["version"] = 1,
                        ["savedAt"] = DateTime.UtcNow.ToString("o"),
                        ["records"] = recordsArray
                    };

                    byte[] bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    {
                        gz.Write(bytes, 0, bytes.Length);
                    }

                    lock (_ioGate)
                    {
                        if (File.Exists(catalogPath)) File.Delete(catalogPath);
                        File.Move(tmpPath, catalogPath);
                    }

                    _isCatalogDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SOURCE-STORE] Failed to flush catalog: {ex.Message}");
                }
            }
        }

        private void LoadCatalog()
        {
            string catalogPath = Path.Combine(_storeDirectory, "catalog.json.gz");
            if (!File.Exists(catalogPath)) return;

            try
            {
                using (var fs = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var root = JObject.Parse(json);
                    var array = root["records"] as JArray;
                    if (array == null) return;

                    foreach (var item in array)
                    {
                        string key = item["k"]?.ToString();
                        string guid = item["g"]?.ToString();
                        string part = item["p"]?.ToString();
                        string uStr = item["u"]?.ToString();
                        DateTime? u = !string.IsNullOrEmpty(uStr) && DateTime.TryParse(uStr, out var parsedU) ? parsedU : (DateTime?)null;
                        string v = item["v"]?.ToString();
                        string h = item["h"]?.ToString();
                        string f = item["f"]?.ToString();
                        long b = item["b"]?.Value<long>() ?? 0;
                        string sStr = item["s"]?.ToString();
                        DateTime s = !string.IsNullOrEmpty(sStr) && DateTime.TryParse(sStr, out var parsedS) ? parsedS : DateTime.UtcNow;

                        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(part))
                        {
                            var summary = new RecordSummary
                            {
                                Guid = guid,
                                PartName = part,
                                LastUpdate = u,
                                VersionToken = v,
                                ContentHash = h,
                                RelativeFilePath = f,
                                FileBytes = b,
                                StoredAtUtc = s
                            };
                            _records[key] = summary;

                            // Reconstruct trigram index lazily or from files if needed
                        }
                    }

                    // Build trigrams in parallel across loaded records
                    Parallel.ForEach(_records, kvp =>
                    {
                        if (TryGet(kvp.Value.Guid, kvp.Value.PartName, out string src) && !string.IsNullOrEmpty(src))
                        {
                            var trigrams = TrigramExtractor.ExtractTrigrams(src);
                            foreach (var t in trigrams)
                            {
                                var set = _trigramIndex.GetOrAdd(t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                                lock (set)
                                {
                                    set.Add(kvp.Key);
                                }
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SOURCE-STORE] Error reading catalog: {ex.Message}");
            }
        }

        private void EnforceStorageBudget()
        {
            long maxBytes = Configuration.SourceStoreMaxMB * 1024L * 1024L;
            long currentBytes = 0;
            foreach (var r in _records.Values) currentBytes += r.FileBytes;

            if (currentBytes <= maxBytes) return;

            // LRU eviction: sort by StoredAtUtc ascending
            var sorted = _records.Values.OrderBy(r => r.StoredAtUtc).ToList();
            long targetBytes = (long)(maxBytes * 0.85);

            foreach (var rec in sorted)
            {
                if (currentBytes <= targetBytes) break;
                string key = MakeKey(rec.Guid, rec.PartName);
                if (_records.TryRemove(key, out _))
                {
                    try
                    {
                        string fullPath = Path.Combine(_storeDirectory, rec.RelativeFilePath);
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        currentBytes -= rec.FileBytes;
                    }
                    catch { }
                }
            }
            MarkCatalogDirty();
        }

        private static string ComputeHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
