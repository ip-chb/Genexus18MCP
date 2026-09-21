using System;
using System.IO;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Active GeneXus installation directory for the Worker: the Gateway-provided
    /// <c>GX_PROGRAM_DIR</c>, then <c>GX_PATH</c>, then the directory of the loaded
    /// SDK assembly. Returns null when none is available.
    /// </summary>
    internal static class GeneXusInstallPath
    {
        public static string Resolve()
        {
            foreach (var candidate in new[]
            {
                Environment.GetEnvironmentVariable("GX_PROGRAM_DIR"),
                Environment.GetEnvironmentVariable("GX_PATH")
            })
            {
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            try
            {
                var sdkAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Architecture.Common", StringComparison.OrdinalIgnoreCase));
                if (sdkAssembly != null && !string.IsNullOrWhiteSpace(sdkAssembly.Location))
                    return Path.GetDirectoryName(sdkAssembly.Location);
            }
            catch { }
            return null;
        }
    }
}
