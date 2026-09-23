using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class RuntimePathsTests
    {
        [Fact]
        public void ResolveRoot_UsesOperationalKeyAndHonorsOverride()
        {
            string localAppData = Path.Combine(Path.GetTempPath(), "gxmcp-runtime-paths-test");
            string rootA = RuntimePaths.ResolveRoot(null, localAppData, "state", "scope-a");
            string rootB = RuntimePaths.ResolveRoot(null, localAppData, "state", "scope-b");
            string configured = Path.Combine(localAppData, "custom-state");

            Assert.NotEqual(rootA, rootB);
            Assert.StartsWith(Path.Combine(Path.GetFullPath(localAppData), "GenexusMCP", "state"), rootA, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(configured), RuntimePaths.ResolveRoot(configured, localAppData, "state", "scope-a"));
        }

        [Fact]
        public void BuildLog_IsWrittenUnderTheResolvedLogsRoot()
        {
            string logsRoot = Path.Combine(Path.GetTempPath(), "gxmcp-build-log-test", Guid.NewGuid().ToString("N"));
            try
            {
                bool written = BuildOutputShaper.TryWriteFullLog("full build output", "task-123", logsRoot, out string path);

                Assert.True(written);
                Assert.Equal(Path.Combine(logsRoot, "build-task-123.log"), path);
                Assert.Equal("full build output", File.ReadAllText(path));
            }
            finally
            {
                if (Directory.Exists(logsRoot)) Directory.Delete(logsRoot, recursive: true);
            }
        }

        [Fact]
        public void LogRotation_KeepsThePreviousLogInTheCurrentLogDirectory()
        {
            string logsRoot = Path.Combine(Path.GetTempPath(), "gxmcp-log-rotation-test", Guid.NewGuid().ToString("N"));
            string currentLog = Path.Combine(logsRoot, "worker_debug.log");
            string previousLog = Path.Combine(logsRoot, "worker_debug.prev.log");
            try
            {
                Directory.CreateDirectory(logsRoot);
                File.WriteAllText(currentLog, "previous contents");

                Logger.RotateExistingLog(currentLog);

                Assert.False(File.Exists(currentLog));
                Assert.Equal("previous contents", File.ReadAllText(previousLog));
                Assert.Equal(logsRoot, Path.GetDirectoryName(previousLog));
            }
            finally
            {
                if (Directory.Exists(logsRoot)) Directory.Delete(logsRoot, recursive: true);
            }
        }

        [Fact]
        public void WorkerInstallDirectoryReferences_AreRestrictedToReviewedReadOnlyOrOverriddenUses()
        {
            string repositoryRoot = FindRepositoryRoot();
            string workerRoot = Path.Combine(repositoryRoot, "src", "GxMcp.Worker");
            var allowList = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Helpers/RuntimePaths.cs"] = "InstallDirectory is exposed for read-only path resolution.",
                ["Services/HealthService.cs"] = "Legacy index path is read-only.",
                ["Services/HistoryService.cs"] = "Legacy history root is read-only lookup only.",
                ["Services/IndexCacheService.cs"] = "Constructor fallback is replaced by the KB-scoped cache path before persistence.",
                ["Utils/ArtifactPathResolver.cs"] = "Base directory is only a resolution base for an explicitly configured relative output root."
            };
            var allowListedWriter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Services/IndexCacheService.cs"
            };
            var writeApi = new Regex(@"\b(?:File\.(?:Write\w*|Append\w*|Move|Create|Delete)|Directory\.(?:CreateDirectory|Move|Delete)|new\s+StreamWriter)\b", RegexOptions.Compiled);
            string[] files = Directory.GetFiles(workerRoot, "*.cs", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                if (file.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || file.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || file.IndexOf("\\publish\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || file.IndexOf("\\TestResults\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                string source = File.ReadAllText(file);
                if (!source.Contains("AppDomain.CurrentDomain.BaseDirectory")) continue;

                string relativePath = file.Substring(workerRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                Assert.True(allowList.TryGetValue(relativePath, out string rationale),
                    "Unreviewed Worker install-directory use: " + relativePath);

                if (writeApi.IsMatch(source))
                {
                    Assert.Contains(relativePath, allowListedWriter);
                    Assert.Contains("_indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"cache\", \"search_index.json\");", source);
                    Assert.Contains("_indexPath = Path.Combine(cacheDir, string.Format(\"index_{0}.json\", hash));", source);
                    Assert.NotEmpty(rationale);
                }
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Genexus18MCP.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
