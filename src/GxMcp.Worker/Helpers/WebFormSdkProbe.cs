using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Defensive SDK metadata dumper. Reads only static type metadata from the GeneXus
    /// SDK assembly that hosts WebFormPart — NO instance method calls, NO field reads,
    /// NO foreign-assembly scans. Writes to a dedicated probe.log next to worker_debug.log
    /// so it survives worker process restarts.
    /// </summary>
    internal static class WebFormSdkProbe
    {
        private static int s_dumped;
        private static readonly object s_lock = new object();
        private static string s_logPath;

        private static string LogPath
        {
            get
            {
                if (s_logPath != null) return s_logPath;
                try
                {
                    s_logPath = Path.Combine(Logger.LogDirectory, "webform-sdk-probe.log");
                }
                catch { s_logPath = Path.Combine(Path.GetTempPath(), "GenexusMCP", "logs", "webform-sdk-probe.log"); }
                return s_logPath;
            }
        }

        private static void Log(string line)
        {
            try
            {
                lock (s_lock)
                {
                    File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
                }
            }
            catch { }
            try { Logger.Info("[SdkProbe] " + line); } catch { }
        }

        public static void DumpOnce(object part)
        {
            try { Logger.Info("[SdkProbe] DumpOnce entered, part is " + (part == null ? "null" : part.GetType().FullName)); } catch { }
            if (part == null) return;
            if (Interlocked.Exchange(ref s_dumped, 1) != 0)
            {
                try { Logger.Info("[SdkProbe] already dumped, skipping"); } catch { }
                return;
            }
            try { Logger.Info("[SdkProbe] proceeding, logPath=" + LogPath); } catch { }
            try { Run(part.GetType()); }
            catch (Exception ex) { Log("OUTER FAIL: " + ex.GetType().Name + " " + ex.Message); }
        }

        private static void Run(Type partType)
        {
            Log("=== BEGIN partType=" + partType.FullName + " ===");
            Log("partAssembly=" + partType.Assembly.GetName().Name + " loc=" + SafeLoc(partType.Assembly));

            DumpType(partType, "WebFormPart");
            for (var bt = partType.BaseType; bt != null && bt != typeof(object); bt = bt.BaseType)
            {
                DumpType(bt, "BASE-PART");
            }

            Type[] siblings;
            try { siblings = partType.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                siblings = rtle.Types.Where(x => x != null).ToArray();
                Log("partial GetTypes: " + rtle.LoaderExceptions.Length + " loader errors (continuing)");
            }
            catch (Exception ex)
            {
                Log("GetTypes failed: " + ex.GetType().Name + " " + ex.Message);
                siblings = new Type[0];
            }

            string[] needles = new[] { "WebTag", "WebFormHelper", "WebFormEditable", "IPropertyDefinition", "PropertyValueConverter", "WebFormControl", "AttributeVariableConverter" };

            // Also scan all loaded assemblies for KBObject / KBObjectPart / dirty-flag candidates.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] all;
                try { all = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { all = rtle.Types.Where(x => x != null).ToArray(); }
                catch { continue; }

                foreach (var ty in all)
                {
                    string n = ty.FullName ?? "";
                    bool isInteresting =
                        n.EndsWith(".KBObject", StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith(".KBObjectPart", StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith(".Entity", StringComparison.OrdinalIgnoreCase) && n.Contains("Architecture") ||
                        n.IndexOf("Entity+", StringComparison.OrdinalIgnoreCase) > 0;
                    if (!isInteresting) continue;
                    DumpType(ty, "SDK-BASE");
                }
            }
            foreach (var ty in siblings)
            {
                string n = ty.FullName ?? "";
                bool match = false;
                foreach (var needle in needles)
                {
                    if (n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                }
                if (!match) continue;
                DumpType(ty, "SDK");
            }

            Log("=== END ===");
        }

        private static void DumpType(Type t, string label)
        {
            try
            {
                Log(">>> " + label + " " + t.FullName + " (interfaces: " + string.Join(",", t.GetInterfaces().Select(i => i.Name)) + ")");
                foreach (var f in SafeFields(t)) Log("   FIELD " + (f.IsStatic ? "static " : "") + Safe(() => f.FieldType.Name) + " " + f.Name);
                foreach (var p in SafeProps(t)) Log("   PROP  " + Safe(() => p.PropertyType.Name) + " " + p.Name + " idx=" + p.GetIndexParameters().Length + " [" + (p.CanRead ? "R" : "") + (p.CanWrite ? "W" : "") + "]");
                foreach (var m in SafeMethods(t)) Log("   METH  " + (m.IsStatic ? "static " : "") + Safe(() => m.ReturnType.Name) + " " + m.Name + "(" + Safe(() => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))) + ")");
            }
            catch (Exception ex)
            {
                Log("DumpType(" + label + " " + t.FullName + ") fail: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static FieldInfo[] SafeFields(Type t) { try { return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { return new FieldInfo[0]; } }
        private static PropertyInfo[] SafeProps(Type t) { try { return t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { return new PropertyInfo[0]; } }
        private static MethodInfo[] SafeMethods(Type t)
        {
            try { return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).ToArray(); }
            catch { return new MethodInfo[0]; }
        }

        private static string Safe(Func<string> fn) { try { return fn(); } catch { return "?"; } }
        private static string SafeLoc(Assembly a) { try { return a.Location; } catch { return "?"; } }
    }
}
