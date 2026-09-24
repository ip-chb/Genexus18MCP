using System;
using System.Diagnostics;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Plan 012 — ReapByPidIfAlive is the PID-based replacement for dereferencing
    /// the already-disposed Process instance in BuildService's guaranteed-cleanup
    /// finally block. These cover the no-op paths and the real reap path.
    /// </summary>
    [Trait("Category", "ProcessSmoke")]
    public class BuildReapByPidTests
    {
        [Fact]
        public void ReapByPidIfAlive_ZeroPid_NoThrowNoOp()
        {
            var svc = new BuildService();
            var ex = Record.Exception(() => svc.ReapByPidIfAlive(0, DateTime.MinValue));
            Assert.Null(ex);
        }

        [Fact]
        public void ReapByPidIfAlive_DeadPid_NoThrowNoOp()
        {
            var svc = new BuildService();
            // A PID that's virtually guaranteed not to be a running process on the
            // test box. Process.GetProcessById throws ArgumentException for it,
            // which ReapByPidIfAlive swallows as a no-op.
            int deadPid = int.MaxValue - 1;
            var ex = Record.Exception(() => svc.ReapByPidIfAlive(deadPid, DateTime.MinValue));
            Assert.Null(ex);
        }

        [Fact]
        public void ReapByPidIfAlive_LiveProcess_KillsIt()
        {
            var svc = new BuildService();
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping 127.0.0.1 -n 30 >nul",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process process = null;
            try
            {
                process = Process.Start(psi);
                Assert.NotNull(process);
                int pid = process.Id;
                DateTime start;
                try { start = process.StartTime; } catch { start = DateTime.MinValue; }

                svc.ReapByPidIfAlive(pid, start);

                bool exited = process.WaitForExit(5000);
                Assert.True(exited, "process should have been reaped within 5s");
            }
            finally
            {
                try
                {
                    if (process != null && !process.HasExited) process.Kill();
                }
                catch { /* best-effort cleanup */ }
                process?.Dispose();
            }
        }
    }
}
