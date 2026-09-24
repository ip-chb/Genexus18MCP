using System;
using System.Diagnostics;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public class GithubServiceTests
    {
        [Fact]
        public void CreatePr_WhenGhUnavailable_ReturnsGhCliNotInstalled()
        {
            // Hide `gh` (and everything else) from PATH so Process.Start("gh") throws Win32Exception.
            var prevPath = Environment.GetEnvironmentVariable("PATH");
            var prevPathExt = Environment.GetEnvironmentVariable("PATHEXT");
            try
            {
                Environment.SetEnvironmentVariable("PATH", string.Empty);
                Environment.SetEnvironmentVariable("PATHEXT", string.Empty);

                var tempCwd = Path.Combine(Path.GetTempPath(), "gxmcp-gh-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempCwd);
                try
                {
                    var svc = new GithubService(kbService: null);
                    var raw = svc.CreatePr("a title", "body", "main", tempCwd);
                    var j = JObject.Parse(raw);

                    Assert.Equal("error", (string)j["status"]);
                    Assert.Equal("GhCliNotInstalled", (string)j["error"]?["code"]);
                    Assert.NotNull(j["error"]?["hint"]);
                }
                finally
                {
                    try { Directory.Delete(tempCwd, recursive: true); } catch { }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", prevPath);
                Environment.SetEnvironmentVariable("PATHEXT", prevPathExt);
            }
        }

        [Fact]
        public void CreatePr_MissingTitle_ReturnsErrorEnvelope()
        {
            var svc = new GithubService(kbService: null);
            var raw = svc.CreatePr("", "body", null, null);
            var j = JObject.Parse(raw);
            Assert.Equal("error", (string)j["status"]);
            Assert.Contains("title is required", (string)j["error"]?["message"]);
        }

        [Fact]
        public void CreatePr_WhenGhInstalledAndExitsNonZero_ReturnsGhExitNonZero()
        {
            if (!IsOnPath("gh"))
                return; // env doesn't have gh — skip silently (test still passes when gh absent).

            var tempCwd = Path.Combine(Path.GetTempPath(), "gxmcp-gh-nogit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempCwd);
            try
            {
                var svc = new GithubService(kbService: null);
                // Running `gh pr create` outside a git repo must fail with non-zero exit.
                var raw = svc.CreatePr("title", "body", null, tempCwd);
                var j = JObject.Parse(raw);
                Assert.Equal("error", (string)j["status"]);
                Assert.Equal("GhExitNonZero", (string)j["error"]?["code"]);
                Assert.NotNull(j["exitCode"]);
                Assert.NotEqual(0, (int)j["exitCode"]);
                Assert.NotNull(j["stderr"]);
                Assert.Equal(tempCwd, (string)j["cwd"]);
            }
            finally
            {
                try { Directory.Delete(tempCwd, recursive: true); } catch { }
            }
        }

        private static bool IsOnPath(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c where " + exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
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
    }
}
