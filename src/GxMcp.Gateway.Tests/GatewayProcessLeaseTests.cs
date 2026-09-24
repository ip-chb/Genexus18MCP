using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public class GatewayProcessLeaseTests
    {
        [Fact]
        public void BuildInstanceKey_ShouldNormalizeEquivalentPaths()
        {
            var configA = CreateConfig(
                @"C:\KBs\Sample\",
                @"C:\Program Files (x86)\GeneXus\GeneXus18\",
                @"C:\KBs\Sample\.gx_mirror\"
            );
            var configB = CreateConfig(
                @"c:\kbs\sample",
                @"c:\program files (x86)\genexus\genexus18",
                @"c:\kbs\sample\.gx_mirror"
            );

            var keyA = GatewayProcessLease.BuildInstanceKey(configA);
            var keyB = GatewayProcessLease.BuildInstanceKey(configB);

            Assert.Equal(keyA, keyB);
        }

        [Fact]
        public void LeaseHeartbeatInterval_MustStayWellUnderStaleWindow()
        {
            // Regression guard for the intermittent "Transport closed" bug: a live
            // master refreshes its lease every LeaseHeartbeatInterval. If that isn't
            // comfortably below LeaseStaleAfter, a newly-spawned gateway treats the
            // live master as stale, re-registers, fails to bind the port, and kills
            // the master. Require room for at least one missed beat.
            Assert.True(
                GatewayProcessLease.LeaseHeartbeatInterval > TimeSpan.Zero,
                "Heartbeat interval must be positive.");
            Assert.True(
                GatewayProcessLease.LeaseHeartbeatInterval * 2 <= GatewayProcessLease.LeaseStaleAfter,
                $"Heartbeat ({GatewayProcessLease.LeaseHeartbeatInterval}) must be <= half the stale window ({GatewayProcessLease.LeaseStaleAfter}).");
        }

        [Fact]
        public void TryRegisterCurrentProcess_ShouldRecoverStaleLease()
        {
            var config = CreateConfig(
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "kb"),
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "gx"),
                null,
                5510
            );
            var leasePath = GatewayProcessLease.GetLeasePath(GatewayProcessLease.BuildInstanceKey(config));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
                File.WriteAllText(
                    leasePath,
                    JsonConvert.SerializeObject(new GatewayLeaseRecord
                    {
                        InstanceKey = GatewayProcessLease.BuildInstanceKey(config),
                        ProcessId = 999999,
                        HttpPort = 5510,
                        KBPath = config.Environment!.KBPath!,
                        ProgramDir = config.GeneXus!.InstallationPath!,
                        ShadowPath = config.Environment.GX_SHADOW_PATH!,
                        UpdatedUtc = DateTime.UtcNow
                    })
                );

                var registration = GatewayProcessLease.TryRegisterCurrentProcess(config);

                Assert.True(registration.Success);
                Assert.NotNull(registration.Lease);
                Assert.Equal(Environment.ProcessId, registration.Lease!.ProcessId);
            }
            finally
            {
                GatewayProcessLease.ReleaseCurrentProcess(config);
                TryDelete(leasePath);
            }
        }

        [Fact]
        public void LeaseWrite_IsAtomic_ProducesCompleteJson_AndLeavesNoTempFiles()
        {
            // The lease is now published via temp-file + atomic rename so a concurrently
            // starting gateway can never read a half-written (partial/empty) lease and wrongly
            // conclude "no master" → startup split-brain. Exercise both write branches
            // (create via Move, overwrite via Replace) through register + refresh, and assert
            // the on-disk lease always parses and no .tmp scratch files are left behind.
            var config = CreateConfig(
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "kb"),
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "gx"),
                null,
                5512
            );
            var leasePath = GatewayProcessLease.GetLeasePath(GatewayProcessLease.BuildInstanceKey(config));
            var leaseDir = Path.GetDirectoryName(leasePath)!;
            string? unrelatedTempPath = null;
            try
            {
                var reg = GatewayProcessLease.TryRegisterCurrentProcess(config);   // create branch
                Assert.True(reg.Success);
                var parsed1 = JsonConvert.DeserializeObject<GatewayLeaseRecord>(File.ReadAllText(leasePath));
                Assert.Equal(Environment.ProcessId, parsed1!.ProcessId);

                GatewayProcessLease.RefreshCurrentProcess(config);                 // overwrite branch
                var parsed2 = JsonConvert.DeserializeObject<GatewayLeaseRecord>(File.ReadAllText(leasePath));
                Assert.Equal(Environment.ProcessId, parsed2!.ProcessId);

                unrelatedTempPath = Path.Combine(leaseDir, "unrelated.tmp." + Guid.NewGuid().ToString("N"));
                File.WriteAllText(unrelatedTempPath, "concurrent gateway scratch file");

                // No scratch file for this lease is left behind. Other gateway
                // instances may legitimately publish their own lease concurrently.
                var leaseTempPattern = Path.GetFileName(leasePath) + ".tmp.*";
                Assert.Empty(Directory.GetFiles(leaseDir, leaseTempPattern));
                Assert.True(File.Exists(unrelatedTempPath));
            }
            finally
            {
                GatewayProcessLease.ReleaseCurrentProcess(config);
                TryDelete(leasePath);
                if (unrelatedTempPath != null)
                    TryDelete(unrelatedTempPath);
            }
        }

        [Fact]
        public void TryRegisterCurrentProcess_ShouldRejectActiveDuplicateLease()
        {
            var config = CreateConfig(
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "kb"),
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "gx"),
                null,
                5511
            );
            var leasePath = GatewayProcessLease.GetLeasePath(GatewayProcessLease.BuildInstanceKey(config));
            using var holder = StartSleeperProcess();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
                File.WriteAllText(
                    leasePath,
                    JsonConvert.SerializeObject(new GatewayLeaseRecord
                    {
                        InstanceKey = GatewayProcessLease.BuildInstanceKey(config),
                        ProcessId = holder.Id,
                        HttpPort = 5511,
                        KBPath = config.Environment!.KBPath!,
                        ProgramDir = config.GeneXus!.InstallationPath!,
                        ShadowPath = config.Environment.GX_SHADOW_PATH!,
                        UpdatedUtc = DateTime.UtcNow
                    })
                );

                var registration = GatewayProcessLease.TryRegisterCurrentProcess(config);

                Assert.False(registration.Success);
                Assert.True(registration.IsDuplicate);
                Assert.NotNull(registration.Lease);
                Assert.Equal(holder.Id, registration.Lease!.ProcessId);
            }
            finally
            {
                TryDelete(leasePath);
                TryStop(holder);
            }
        }

        private static Configuration CreateConfig(string kbPath, string installationPath, string? shadowPath, int httpPort = 5500)
        {
            var effectiveShadow = shadowPath ?? Path.Combine(kbPath, ".gx_mirror");
            return new Configuration
            {
                Server = new ServerConfig
                {
                    HttpPort = httpPort,
                    BindAddress = "127.0.0.1",
                    McpStdio = false
                },
                GeneXus = new GeneXusConfig
                {
                    InstallationPath = installationPath,
                    WorkerExecutable = "worker\\GxMcp.Worker.exe"
                },
                Environment = new EnvironmentConfig
                {
                    KBPath = kbPath,
                    GX_SHADOW_PATH = effectiveShadow
                }
            };
        }

        private static Process StartSleeperProcess()
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                throw new InvalidOperationException("Could not start sleeper process for lease test.");
            }

            return process;
        }

        private static void TryStop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
