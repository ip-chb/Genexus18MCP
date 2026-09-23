using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    internal static class RuntimePaths
    {
        internal static string InstallDirectory => AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;

        internal static string StateRoot => ResolveRoot(
            Environment.GetEnvironmentVariable("GXMCP_STATE_DIR"),
            GetLocalAppData(),
            "state",
            GetOperationalStateKey());

        internal static string LogsRoot => ResolveRoot(
            Environment.GetEnvironmentVariable("GXMCP_LOG_DIR"),
            GetLocalAppData(),
            "logs",
            GetOperationalStateKey());

        internal static string TempRoot => ResolveRoot(
            null,
            GetLocalAppData(),
            "tmp",
            GetOperationalStateKey());

        internal static string ResolveRoot(string configuredRoot, string localAppData, string component, string operationalStateKey)
        {
            if (!string.IsNullOrWhiteSpace(configuredRoot))
                return Path.GetFullPath(configuredRoot.Trim());

            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Path.GetTempPath();
            if (string.IsNullOrWhiteSpace(component) || component == "." || component == "..")
                throw new ArgumentException("A runtime path component is required.", nameof(component));

            return Path.GetFullPath(Path.Combine(
                localAppData,
                "GenexusMCP",
                component,
                HashKey(operationalStateKey)));
        }

        private static string GetLocalAppData()
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path;
        }

        private static string GetOperationalStateKey()
        {
            string key = Environment.GetEnvironmentVariable("GXMCP_OPERATIONAL_STATE_KEY");
            if (!string.IsNullOrWhiteSpace(key)) return key;

            string kbId = Environment.GetEnvironmentVariable("GXMCP_KB_ID");
            if (string.IsNullOrWhiteSpace(kbId)) kbId = Environment.GetEnvironmentVariable("GX_KB_PATH");
            string generation = Environment.GetEnvironmentVariable("GXMCP_KB_GENERATION") ?? "0";
            return string.IsNullOrWhiteSpace(kbId) ? "standalone" : kbId + "|" + generation;
        }

        private static string HashKey(string key)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? "standalone"));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
