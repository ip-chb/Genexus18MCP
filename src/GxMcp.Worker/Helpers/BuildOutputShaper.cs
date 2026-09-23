using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    // v2.6.6 Stream C: MSBuild output shaping for the LLM agent.
    //
    // FR#22: status.Output reaches 200+ KB per build and gets truncated mid-stream
    //        in the JSON-RPC envelope. We cap to head/tail and pin the full log on
    //        disk under publish/worker/logs/build-<taskId>.log for later retrieval.
    //
    // FR#23: spc0022 / spc0158 / CS0246 warnings repeat dozens of times — collapse
    //        into [{code, count, sample, objects}] groups when compact=true.
    //
    // FR#21: MSBuild reports errors at GxBuild_xxx.msbuild(5,5); rewrite to
    //        [gx-object=... phase=...] so the agent gets the actionable target.
    public static class BuildOutputShaper
    {
        public const int HeadBytes = 8 * 1024;
        public const int TailBytes = 24 * 1024;

        public class ShapedOutput
        {
            public string head { get; set; }
            public string tail { get; set; }
            public string hint { get; set; }
            public int total_lines { get; set; }
            public int dropped_lines { get; set; }
            public string full_log_path { get; set; }
        }

        public class WarningGroup
        {
            public string code { get; set; }
            public int count { get; set; }
            public string sample { get; set; }
            public List<string> objects { get; set; } = new List<string>();
        }

        public static ShapedOutput Shape(string fullOutput, int totalLines, string fullLogPath)
        {
            var so = new ShapedOutput
            {
                total_lines = totalLines,
                full_log_path = fullLogPath,
                hint = "Full log at " + (fullLogPath ?? "<unavailable>")
            };
            if (string.IsNullOrEmpty(fullOutput))
            {
                so.head = string.Empty;
                so.tail = string.Empty;
                return so;
            }

            int len = fullOutput.Length;
            if (len <= HeadBytes + TailBytes)
            {
                so.head = fullOutput;
                so.tail = string.Empty;
                so.dropped_lines = 0;
                return so;
            }

            so.head = fullOutput.Substring(0, HeadBytes);
            so.tail = fullOutput.Substring(len - TailBytes, TailBytes);

            // Approximate dropped line count from the middle slice we elided.
            int middleStart = HeadBytes;
            int middleLen = len - HeadBytes - TailBytes;
            int dropped = 0;
            for (int i = middleStart; i < middleStart + middleLen; i++)
            {
                if (fullOutput[i] == '\n') dropped++;
            }
            so.dropped_lines = dropped;
            return so;
        }

        public static bool TryWriteFullLog(string fullOutput, string taskId, out string path)
        {
            return TryWriteFullLog(fullOutput, taskId, RuntimePaths.LogsRoot, out path);
        }

        internal static bool TryWriteFullLog(string fullOutput, string taskId, string logsDir, out string path)
        {
            path = null;
            try
            {
                Directory.CreateDirectory(logsDir);
                path = Path.Combine(logsDir, "build-" + taskId + ".log");
                File.WriteAllText(path, fullOutput ?? string.Empty);
                // Retention sweep (never breaks the write — SweepOldBuildLogs is total).
                SweepOldBuildLogs(logsDir, ResolveBuildLogRetainCount());
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Log retention (worker disk weight). Every build pins its full output to
        // build-<taskId>.log; without a sweep a long session litters logs/ forever.
        // Keeps the newest retainCount build-*.log files, deletes the rest. Only
        // build-*.log is ever touched; <=0 disables the sweep.
        internal static int ResolveBuildLogRetainCount()
        {
            int def = 50;
            var raw = Environment.GetEnvironmentVariable("GXMCP_BUILD_LOG_RETAIN_COUNT");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var v))
            {
                if (v <= 0) return int.MaxValue; // explicit disable
                def = v;
            }
            return def;
        }

        internal static int SweepOldBuildLogs(string logsDir, int retainCount)
        {
            int deleted = 0;
            try
            {
                if (string.IsNullOrEmpty(logsDir) || !Directory.Exists(logsDir)) return 0;
                if (retainCount <= 0) return 0;
                if (retainCount == int.MaxValue) return 0;
                var files = new DirectoryInfo(logsDir).GetFiles("build-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();
                for (int i = retainCount; i < files.Count; i++)
                {
                    try { files[i].Delete(); deleted++; } catch { }
                }
            }
            catch { }
            return deleted;
        }

        // Match codes like spc0022, gen0010, CS0246, MSB4131. Capture group 1 = code.
        private static readonly Regex _rxCode = new Regex(
            @"\b((?:spc|gen|CS|MSB)\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Heuristic to pull a GX object name out of a warning line. Looks for
        // common patterns: "Warning in 'X'", "object 'X'", "on 'X'". The earlier
        // alternation `(?:in|object|on)\s+['"]?(\w+)` greedy-matched on `in` and
        // captured the literal next token (e.g. captured "object" for "in object 'X'"),
        // so we now require a quoted name and accept "in object" as a two-token prefix.
        private static readonly Regex _rxObjectHint = new Regex(
            @"(?:in\s+object|object|on|in)\s+['""]([A-Za-z_][\w]{1,63})['""]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<WarningGroup> AggregateWarnings(IEnumerable<string> warnings)
        {
            var result = new List<WarningGroup>();
            if (warnings == null) return result;

            var byCode = new Dictionary<string, WarningGroup>(StringComparer.OrdinalIgnoreCase);
            var uncoded = new WarningGroup { code = "(uncoded)", count = 0 };

            foreach (var raw in warnings)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var w = raw.Trim();
                var cm = _rxCode.Match(w);
                WarningGroup g;
                if (cm.Success)
                {
                    string code = cm.Groups[1].Value.ToLowerInvariant();
                    if (!byCode.TryGetValue(code, out g))
                    {
                        g = new WarningGroup { code = code, sample = w };
                        byCode[code] = g;
                    }
                }
                else
                {
                    g = uncoded;
                    if (g.sample == null) g.sample = w;
                }
                g.count++;

                var om = _rxObjectHint.Match(w);
                if (om.Success)
                {
                    var obj = om.Groups[1].Value;
                    if (!g.objects.Contains(obj, StringComparer.OrdinalIgnoreCase) && g.objects.Count < 20)
                        g.objects.Add(obj);
                }
            }

            result.AddRange(byCode.Values.OrderByDescending(g => g.count));
            if (uncoded.count > 0) result.Add(uncoded);
            return result;
        }

        // FR#21: rewrite "GxBuild_xxx.msbuild(N,M): error : ..." to embed the
        // GX object + phase so the agent doesn't chase a temp-file location.
        private static readonly Regex _rxGxBuildLoc = new Regex(
            @"GxBuild_[A-Za-z0-9]+\.msbuild\(\d+,\d+\)",
            RegexOptions.Compiled);

        public static bool TryRewriteErrorLocation(string line, string currentObject, string phase, out string rewritten)
        {
            rewritten = line;
            if (string.IsNullOrEmpty(line)) return false;
            if (!_rxGxBuildLoc.IsMatch(line)) return false;
            if (string.IsNullOrEmpty(currentObject)) return false;

            string tag = "[gx-object=" + currentObject + " phase=" + (phase ?? "?") + "]";
            rewritten = _rxGxBuildLoc.Replace(line, tag);
            return true;
        }
    }
}
