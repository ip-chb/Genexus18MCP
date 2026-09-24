using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public class TimeTravelServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public TimeTravelServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-tt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            // Git keeps read-only files in .git/objects — clear attributes before delete.
            try { ForceDelete(_tempDir); } catch { }
        }

        private static void ForceDelete(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(root, recursive: true);
        }

        // Stand-in for an opened GeneXus KB. The real KbService stores a dynamic
        // `_kb` whose `.Location` is read via the DLR — any object with a public
        // `Location` property satisfies the dynamic dispatch.
        public class FakeKb
        {
            public string Location { get; set; }
        }

        private static KbService BuildKbServiceWithPath(string path)
        {
            var indexCache = new IndexCacheService();
            var kb = new KbService(indexCache);
            // Inject our fake KB into the private `_kb` field so GetKbPath() returns `path`.
            var fld = typeof(KbService).GetField("_kb", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            fld.SetValue(kb, new FakeKb { Location = path });
            return kb;
        }

        [Fact]
        public void Recover_EmptyName_ReturnsErrorEnvelope()
        {
            var svc = new TimeTravelService(kbService: null, objectService: null);
            var j = JObject.Parse(svc.Recover("", "abc1234"));
            Assert.Equal("error", (string)j["status"]);
            Assert.Equal("name is required.", (string)j["error"]["message"]);
        }

        [Fact]
        public void Recover_EmptyAt_ReturnsErrorEnvelope()
        {
            var svc = new TimeTravelService(kbService: null, objectService: null);
            var j = JObject.Parse(svc.Recover("MyObj", ""));
            Assert.Equal("error", (string)j["status"]);
            Assert.Equal("at is required (ISO timestamp or commit sha).", (string)j["error"]["message"]);
        }

        [Fact]
        public void Recover_KbWithoutGit_ReturnsKbNotInGit()
        {
            // _tempDir exists but has no .git/ subdir.
            var kbSvc = BuildKbServiceWithPath(_tempDir);
            var svc = new TimeTravelService(kbSvc, objectService: null);
            var j = JObject.Parse(svc.Recover("MyObj", "abc1234"));
            Assert.Equal("error", (string)j["status"]);
            Assert.Equal("KbNotInGit", (string)j["error"]["code"]);
        }

        [Fact]
        public void Recover_HappyPath_WithRealGitRepo_ReturnsParts()
        {
            if (!IsGitAvailable()) return; // skip if git missing

            // Init a real git repo, create Objects/Procedure/MyProc/MyProc.txt, commit.
            RunGit(_tempDir, "init");
            RunGit(_tempDir, "config user.email t@t.local");
            RunGit(_tempDir, "config user.name test");

            var objDir = Path.Combine(_tempDir, "Objects", "Procedure", "MyProc");
            Directory.CreateDirectory(objDir);
            var partPath = Path.Combine(objDir, "Source.txt");
            File.WriteAllText(partPath, "msg('hello world')");

            RunGit(_tempDir, "add -A");
            RunGit(_tempDir, "-c user.name=test -c user.email=t@t.local commit -m initial");

            // Grab the commit sha.
            int rc = RunGitCapture(_tempDir, "rev-parse HEAD", out string head, out string _);
            Assert.Equal(0, rc);
            string sha = head.Trim();
            Assert.True(sha.Length >= 7, "expected commit sha, got: " + head);

            var kbSvc = BuildKbServiceWithPath(_tempDir);
            // Pass null ObjectService — TimeTravelService falls back to walking Objects/<type>/<name>/.
            var svc = new TimeTravelService(kbSvc, objectService: null);

            var j = JObject.Parse(svc.Recover("MyProc", sha));
            Assert.Equal("ok", (string)j["status"]);
            Assert.Equal("MyProc", (string)j["target"]);
            Assert.Equal(sha, (string)j["result"]["recoveredFromCommit"]);

            var parts = (JArray)j["result"]["parts"];
            Assert.NotNull(parts);
            Assert.True(parts.Count >= 1, "expected at least one recovered part, got " + parts.Count);
            Assert.Contains(parts, p => ((string)p["content"]).Contains("hello world"));
        }

        private static bool IsGitAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c where git")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(3000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static int RunGit(string cwd, string args)
        {
            return RunGitCapture(cwd, args, out _, out _);
        }

        private static int RunGitCapture(string cwd, string args, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) { stdout = ""; stderr = "null process"; return -1; }
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode;
            }
        }
    }
}
