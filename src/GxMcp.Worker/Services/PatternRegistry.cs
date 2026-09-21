using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// One installed GeneXus pattern, read from its <c>.Pattern</c> manifest
    /// (<c>&lt;GX&gt;\Packages\Patterns\&lt;Name&gt;\&lt;Name&gt;.Pattern</c>).
    /// </summary>
    public sealed class PatternManifest
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        /// <summary>Instance object name template, e.g. <c>K2BEntityServices{0}</c>; may have no placeholder.</summary>
        public string InstanceNameTemplate { get; set; }
        /// <summary>Parent object types accepted by the pattern. Empty when the manifest declares <c>(None)</c>.</summary>
        public IReadOnlyList<string> ParentObjectTypes { get; set; } = new string[0];
        public string ManifestPath { get; set; }
        public IReadOnlyList<string> Aliases { get; set; } = new string[0];

        public bool IsWorkWithPlus => Id == PatternRegistry.WorkWithPlusPatternId;

        /// <summary>True when the manifest declares no parent object (e.g. K2BMenu), so it cannot be applied to an object.</summary>
        public bool IsParentless => ParentObjectTypes.Count == 0;

        public bool AcceptsParentType(string parentType)
        {
            if (string.IsNullOrWhiteSpace(parentType)) return false;
            return ParentObjectTypes.Any(t => string.Equals(t, parentType, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Expected instance object name for a parent, or null when the template is unknown.</summary>
        public string FormatInstanceName(string parentName)
        {
            if (string.IsNullOrWhiteSpace(InstanceNameTemplate)) return null;
            if (InstanceNameTemplate.IndexOf("{0}", StringComparison.Ordinal) < 0) return InstanceNameTemplate;
            return InstanceNameTemplate.Replace("{0}", parentName ?? string.Empty);
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["name"] = Name,
                ["id"] = Id.ToString(),
                ["publisher"] = Publisher ?? "",
                ["version"] = Version ?? "",
                ["instanceName"] = InstanceNameTemplate ?? "",
                ["parentObjects"] = new JArray(ParentObjectTypes.ToArray())
            };
        }
    }

    /// <summary>
    /// Patterns installed in the active GeneXus installation, discovered from the
    /// <c>.Pattern</c> manifests. WorkWithPlus is always registered (alias <c>WWP</c>)
    /// so existing WWP callers keep resolving even when its manifest is absent.
    /// Discovery is cached per installation path: packages do not change under a
    /// running Worker.
    /// </summary>
    public sealed class PatternRegistry
    {
        public static readonly Guid WorkWithPlusPatternId = new Guid("07135890-56fc-489b-b408-063722fa9f7d");

        private static readonly ConcurrentDictionary<string, PatternRegistry> Cache =
            new ConcurrentDictionary<string, PatternRegistry>(StringComparer.OrdinalIgnoreCase);

        private readonly List<PatternManifest> _patterns;

        public PatternRegistry(IEnumerable<PatternManifest> discovered)
        {
            _patterns = new List<PatternManifest>();
            foreach (var m in discovered ?? Enumerable.Empty<PatternManifest>())
            {
                if (m == null || m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.Name)) continue;
                if (_patterns.Any(p => p.Id == m.Id)) continue;
                _patterns.Add(m);
            }

            var wwp = _patterns.FirstOrDefault(p => p.Id == WorkWithPlusPatternId);
            if (wwp == null)
            {
                wwp = new PatternManifest
                {
                    Id = WorkWithPlusPatternId,
                    Name = "WorkWithPlus",
                    Publisher = "DVelop",
                    InstanceNameTemplate = "WorkWithPlus{0}",
                    ParentObjectTypes = new[] { "Transaction", "WebPanel", "WebComponent", "SDPanel" }
                };
                _patterns.Add(wwp);
            }
            wwp.Aliases = new[] { "WWP" };
        }

        public IReadOnlyList<PatternManifest> All => _patterns;

        /// <summary>Registry for the active installation (see <see cref="GeneXusInstallPath.Resolve"/>).</summary>
        public static PatternRegistry Current => ForInstallation(GeneXusInstallPath.Resolve());

        public static PatternRegistry ForInstallation(string installPath)
        {
            string key = string.IsNullOrWhiteSpace(installPath) ? "(none)" : Path.GetFullPath(installPath);
            return Cache.GetOrAdd(key, _ => new PatternRegistry(Discover(installPath)));
        }

        /// <summary>Scans <c>Packages\Patterns\*\*.Pattern</c>. Malformed manifests are logged and skipped.</summary>
        public static IReadOnlyList<PatternManifest> Discover(string installPath)
        {
            var result = new List<PatternManifest>();
            if (string.IsNullOrWhiteSpace(installPath)) return result;
            string root = Path.Combine(installPath, "Packages", "Patterns");
            if (!Directory.Exists(root)) return result;

            IEnumerable<string> files;
            try { files = Directory.EnumerateDirectories(root).SelectMany(d => Directory.EnumerateFiles(d, "*.Pattern")).ToList(); }
            catch (Exception ex)
            {
                Logger.Warn("[PatternRegistry] cannot enumerate " + root + ": " + ex.Message);
                return result;
            }

            foreach (string file in files)
            {
                try
                {
                    var manifest = ParseManifest(File.ReadAllText(file), file);
                    if (manifest != null) result.Add(manifest);
                    else Logger.Warn("[PatternRegistry] skipped manifest without Id/Name: " + file);
                }
                catch (Exception ex)
                {
                    Logger.Warn("[PatternRegistry] skipped malformed manifest " + file + ": " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Parses one manifest. Returns null when it lacks a valid Id or Name; throws only on malformed XML.</summary>
        public static PatternManifest ParseManifest(string xml, string path)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "Pattern", StringComparison.OrdinalIgnoreCase)) return null;

            if (!Guid.TryParse((string)root.Attribute("Id"), out Guid id)) return null;
            string name = ((string)root.Attribute("Name"))?.Trim();
            if (string.IsNullOrEmpty(name)) return null;

            var definition = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Definition");
            string instanceName = definition?.Elements().FirstOrDefault(e => e.Name.LocalName == "InstanceName")?.Value?.Trim();
            var parents = new List<string>();
            var parentObjects = definition?.Elements().FirstOrDefault(e => e.Name.LocalName == "ParentObjects");
            if (parentObjects != null)
            {
                foreach (var p in parentObjects.Elements().Where(e => e.Name.LocalName == "ParentObject"))
                {
                    string type = ((string)p.Attribute("Type"))?.Trim();
                    if (string.IsNullOrEmpty(type) || string.Equals(type, "(None)", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parents.Contains(type, StringComparer.OrdinalIgnoreCase)) parents.Add(type);
                }
            }

            return new PatternManifest
            {
                Id = id,
                Name = name,
                Publisher = (string)root.Attribute("Publisher"),
                Version = (string)root.Attribute("Version"),
                InstanceNameTemplate = instanceName,
                ParentObjectTypes = parents,
                ManifestPath = path
            };
        }

        /// <summary>Resolves a pattern key: name or alias (case-insensitive), then GUID. An unregistered GUID resolves to a bare entry.</summary>
        public bool TryResolve(string key, out PatternManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(key)) return false;
            string k = key.Trim();
            manifest = _patterns.FirstOrDefault(p =>
                string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase) ||
                p.Aliases.Any(a => string.Equals(a, k, StringComparison.OrdinalIgnoreCase)));
            if (manifest != null) return true;

            if (!Guid.TryParse(k, out Guid id)) return false;
            manifest = FindById(id) ?? new PatternManifest { Id = id, Name = id.ToString() };
            return true;
        }

        public PatternManifest FindById(Guid id) => _patterns.FirstOrDefault(p => p.Id == id);

        /// <summary>
        /// Registered pattern whose instance objects have this type: the instance type
        /// name equals the pattern name (WorkWithPlus, K2BEntityServices, ...) and the
        /// instance type GUID equals the pattern Id.
        /// </summary>
        public PatternManifest MatchInstanceType(string typeName, Guid typeGuid)
        {
            if (typeGuid != Guid.Empty)
            {
                var byId = FindById(typeGuid);
                if (byId != null) return byId;
            }
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            return _patterns.FirstOrDefault(p => string.Equals(p.Name, typeName, StringComparison.OrdinalIgnoreCase));
        }

        public JArray ToJson() => new JArray(_patterns.Select(p => p.ToJson()));

        public IList<string> Names() => _patterns.Select(p => p.Name).ToList();
    }
}
