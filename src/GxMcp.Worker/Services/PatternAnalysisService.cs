using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>Identity of an object considered during pattern instance resolution.</summary>
    internal sealed class PatternInstanceCandidate
    {
        public PatternInstanceCandidate(string name, string typeName, Guid typeGuid, object source = null)
        {
            Name = name;
            TypeName = typeName;
            TypeGuid = typeGuid;
            Source = source;
        }

        public string Name { get; }
        public string TypeName { get; }
        public Guid TypeGuid { get; }
        /// <summary>The KBObject this candidate was built from; null in unit tests.</summary>
        public object Source { get; }
    }

    internal sealed class PatternInstanceMatch
    {
        public PatternInstanceMatch(PatternInstanceCandidate candidate, PatternManifest pattern)
        {
            Candidate = candidate;
            Pattern = pattern;
        }

        public PatternInstanceCandidate Candidate { get; }
        public PatternManifest Pattern { get; }
    }

    internal enum PatternInstanceSelectionStatus { Selected, NotFound, Ambiguous, Mismatch }

    internal sealed class PatternInstanceSelection
    {
        public PatternInstanceSelectionStatus Status { get; private set; }
        public PatternInstanceMatch Selected { get; private set; }
        public IReadOnlyList<PatternInstanceMatch> Candidates { get; private set; } = new PatternInstanceMatch[0];
        public PatternManifest RequestedPattern { get; private set; }

        public static PatternInstanceSelection Select(PatternInstanceMatch match) =>
            new PatternInstanceSelection { Status = PatternInstanceSelectionStatus.Selected, Selected = match };

        public static PatternInstanceSelection NotFound(PatternManifest requestedPattern) =>
            new PatternInstanceSelection { Status = PatternInstanceSelectionStatus.NotFound, RequestedPattern = requestedPattern };

        public static PatternInstanceSelection Ambiguous(IReadOnlyList<PatternInstanceMatch> candidates) =>
            new PatternInstanceSelection { Status = PatternInstanceSelectionStatus.Ambiguous, Candidates = candidates };

        public static PatternInstanceSelection Mismatch(PatternInstanceMatch actual, PatternManifest requestedPattern) =>
            new PatternInstanceSelection
            {
                Status = PatternInstanceSelectionStatus.Mismatch,
                Candidates = new[] { actual },
                RequestedPattern = requestedPattern
            };

        /// <summary>Structured diagnostic for ambiguity or pattern mismatch; null for other outcomes.</summary>
        public JObject ToDiagnostic(string objectName)
        {
            if (Status == PatternInstanceSelectionStatus.Ambiguous)
            {
                return new JObject
                {
                    ["code"] = "PatternInstanceAmbiguous",
                    ["message"] = "'" + objectName + "' has " + Candidates.Count + " pattern instances; name the instance to read or edit.",
                    ["objectName"] = objectName,
                    ["candidates"] = new JArray(Candidates.Select(c => new JObject
                    {
                        ["name"] = c.Candidate.Name,
                        ["pattern"] = c.Pattern.Name,
                        ["patternId"] = c.Pattern.Id.ToString()
                    }))
                };
            }

            if (Status == PatternInstanceSelectionStatus.Mismatch && Candidates.Count > 0)
            {
                var actual = Candidates[0];
                return new JObject
                {
                    ["code"] = "PatternMismatch",
                    ["message"] = "'" + objectName + "' is a " + actual.Pattern.Name + " instance, not a " + (RequestedPattern?.Name ?? "requested pattern") + " instance.",
                    ["objectName"] = objectName,
                    ["objectPattern"] = actual.Pattern.Name,
                    ["objectPatternId"] = actual.Pattern.Id.ToString(),
                    ["requestedPattern"] = RequestedPattern?.Name,
                    ["requestedPatternId"] = RequestedPattern?.Id.ToString()
                };
            }

            return null;
        }
    }

    public class PatternAnalysisService
    {
        private static readonly Guid PatternInstancePartGuid = new Guid("a51ced48-7bee-0001-ab12-04e9e32123d1");
        private readonly ObjectService _objectService;
        private PatternRegistry _registry;

        public PatternAnalysisService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        internal PatternAnalysisService(ObjectService objectService, PatternRegistry registry)
        {
            _objectService = objectService;
            _registry = registry;
        }

        /// <summary>Installed patterns; defaults lazily to the active installation's registry.</summary>
        internal PatternRegistry Registry
        {
            get => _registry ?? (_registry = PatternRegistry.Current);
            set => _registry = value;
        }

        /// <summary>Registered pattern the object is an instance of, or null.</summary>
        internal PatternManifest MatchInstancePattern(KBObject obj)
        {
            if (obj == null) return null;
            try { return Registry.MatchInstanceType(obj.TypeDescriptor?.Name, GetTypeGuid(obj)); }
            catch { return null; }
        }

        public string GetWWPStructure(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "The requested object is not available in the active Knowledge Base.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_search",
                        args: new JObject { ["query"] = target },
                        why: "Search for objects matching the name to find the correct identifier.")),
                    target: target);

                // Fast type guard — WWP only applies to WorkWithPlus instances or to
                // Transaction/WebPanel parents that may own one. For Procedure/SDT/
                // Domain/etc., ResolveWWPInstance would still walk model.Objects.GetAll()
                // (10s+ on large KBs) only to return null. Reject upfront.
                string typeName = obj.TypeDescriptor?.Name ?? "";
                bool wwpEligible = string.Equals(typeName, "WorkWithPlus", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(typeName, "Transaction", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(typeName, "WebPanel", StringComparison.OrdinalIgnoreCase);
                if (!wwpEligible)
                {
                    return Models.McpResponse.Err(
                        code: "TypeNotEligibleForWWP",
                        message: $"Object type not eligible for WorkWithPlus pattern.",
                        hint: $"pattern_metadata applies to WorkWithPlus / Transaction / WebPanel objects; '{target}' is a {typeName}.",
                        nextSteps: new JArray(Models.McpResponse.NextStep(
                            tool: "genexus_inspect",
                            args: new JObject { ["name"] = target },
                            why: "Inspect the object to confirm its type and available parts.")),
                        target: target,
                        extra: new JObject { ["objectName"] = obj.Name, ["objectType"] = typeName });
                }

                KBObject instanceObj = ResolveWWPInstance(obj);
                if (instanceObj == null) return Models.McpResponse.Err(
                    code: "WWPInstanceNotFound",
                    message: "WorkWithPlus instance not found.",
                    hint: "No WorkWithPlus instance was resolved for the requested object.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject { ["type"] = "WorkWithPlus" },
                        why: "List all WorkWithPlus instances to find the one associated with this object.")),
                    target: target);

                var part = FindPatternPart(instanceObj, "PatternInstance");
                if (part == null) return Models.McpResponse.Err(
                    code: "PatternInstancePartNotFound",
                    message: "PatternInstance part not found.",
                    hint: "The WorkWithPlus instance does not expose a PatternInstance part.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = instanceObj.Name },
                        why: "Returns availableParts so you can identify the correct part name.")),
                    target: target,
                    extra: new JObject
                    {
                        ["objectName"] = instanceObj.Name,
                        ["objectType"] = instanceObj.TypeDescriptor?.Name,
                        ["availableParts"] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(instanceObj))
                    });

                string xml = ExtractEditablePatternXml(part, instanceObj);
                if (string.IsNullOrEmpty(xml)) return Models.McpResponse.Err(
                    code: "PatternInstanceXmlUnavailable",
                    message: "PatternInstance XML not available.",
                    hint: "The PatternInstance content could not be extracted from the resolved WorkWithPlus instance.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = instanceObj.Name, ["part"] = "PatternInstance" },
                        why: "Attempt a direct part read to surface the raw content or a more specific error.")),
                    target: target,
                    extra: new JObject { ["objectName"] = instanceObj.Name, ["objectType"] = instanceObj.TypeDescriptor?.Name });

                var result = ParseWWPXml(xml);
                result["resolvedObject"] = instanceObj.Name;
                result["resolvedType"] = instanceObj.TypeDescriptor?.Name;
                result["rawSnippet"] = xml.Length > 5000 ? xml.Substring(0, 5000) : xml;
                return Models.McpResponse.Ok(target: target, code: "PatternMetadataRead", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "PatternMetadataFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log; the WorkWithPlus object or PatternInstance XML may be corrupt.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "index", ["force"] = true },
                        why: "Rebuilds the index which may fix object resolution failures.")),
                    target: target);
            }
        }

        public static bool IsPatternPart(string partName)
        {
            return string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "PatternVirtual", StringComparison.OrdinalIgnoreCase);
        }

        public KBObject ResolveWWPInstance(KBObject obj)
        {
            if (obj == null) return null;
            if (obj.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase)) return obj;

            var model = obj.Model;
            if (model == null) return null;

            string instanceName = "WorkWithPlus" + obj.Name;
            var namedMatch = _objectService?.FindObject(instanceName, "WorkWithPlus");
            if (namedMatch != null) return namedMatch;

            try
            {
                var childMatch = model.Objects.GetChildren(obj)
                    .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (childMatch != null) return childMatch;
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// Pure pattern instance selection over already gathered candidates (issue #260).
        /// <list type="number">
        /// <item>The requested object is itself a registered pattern instance: select it,
        /// or report a mismatch when <paramref name="patternId"/> names another pattern.</item>
        /// <item>With <paramref name="patternId"/>: the candidate named by the pattern's
        /// instance template, else any candidate of that pattern.</item>
        /// <item>Without it: WorkWithPlus first (template-named, then any WWP child), else the
        /// single registered-pattern candidate; several are ambiguous.</item>
        /// </list>
        /// </summary>
        internal static PatternInstanceSelection SelectPatternInstance(
            PatternInstanceCandidate requested,
            IReadOnlyList<PatternInstanceCandidate> children,
            Guid? patternId,
            PatternRegistry registry)
        {
            registry = registry ?? new PatternRegistry(null);

            PatternManifest wanted = null;
            if (patternId.HasValue)
                wanted = registry.FindById(patternId.Value) ?? new PatternManifest { Id = patternId.Value, Name = patternId.Value.ToString() };

            if (requested != null)
            {
                var own = registry.MatchInstanceType(requested.TypeName, requested.TypeGuid);
                if (own != null)
                {
                    var self = new PatternInstanceMatch(requested, own);
                    return wanted != null && own.Id != wanted.Id
                        ? PatternInstanceSelection.Mismatch(self, wanted)
                        : PatternInstanceSelection.Select(self);
                }
            }

            var matches = new List<PatternInstanceMatch>();
            foreach (var child in children ?? new PatternInstanceCandidate[0])
            {
                if (child == null || string.IsNullOrWhiteSpace(child.Name)) continue;
                var pattern = registry.MatchInstanceType(child.TypeName, child.TypeGuid);
                if (pattern == null && wanted != null && child.TypeGuid == wanted.Id) pattern = wanted;
                if (pattern == null) continue;
                if (matches.Any(m => m.Pattern.Id == pattern.Id &&
                                     string.Equals(m.Candidate.Name, child.Name, StringComparison.OrdinalIgnoreCase))) continue;
                matches.Add(new PatternInstanceMatch(child, pattern));
            }

            string parentName = requested?.Name;
            if (wanted != null)
            {
                var found = PreferTemplateNamed(matches.Where(m => m.Pattern.Id == wanted.Id).ToList(), wanted, parentName);
                return found != null ? PatternInstanceSelection.Select(found) : PatternInstanceSelection.NotFound(wanted);
            }

            var wwp = PreferTemplateNamed(matches.Where(m => m.Pattern.IsWorkWithPlus).ToList(),
                registry.FindById(PatternRegistry.WorkWithPlusPatternId), parentName);
            if (wwp != null) return PatternInstanceSelection.Select(wwp);

            if (matches.Count == 1) return PatternInstanceSelection.Select(matches[0]);
            if (matches.Count > 1) return PatternInstanceSelection.Ambiguous(matches);
            return PatternInstanceSelection.NotFound(null);
        }

        private static PatternInstanceMatch PreferTemplateNamed(List<PatternInstanceMatch> ofPattern, PatternManifest pattern, string parentName)
        {
            if (ofPattern.Count == 0) return null;
            string expected = pattern?.FormatInstanceName(parentName);
            if (!string.IsNullOrWhiteSpace(expected))
            {
                var named = ofPattern.FirstOrDefault(m => string.Equals(m.Candidate.Name, expected, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
            }
            return ofPattern[0];
        }

        private static Guid GetTypeGuid(KBObject obj)
        {
            try { return obj?.TypeDescriptor?.Id ?? Guid.Empty; }
            catch { return Guid.Empty; }
        }

        private static PatternInstanceCandidate ToCandidate(KBObject obj) =>
            obj == null ? null : new PatternInstanceCandidate(obj.Name, obj.TypeDescriptor?.Name, GetTypeGuid(obj), obj);

        /// <summary>
        /// Resolves the pattern instance that owns the pattern parts of <paramref name="obj"/>
        /// for any registered pattern (see <see cref="SelectPatternInstance"/>). With
        /// <paramref name="fresh"/> the resolved instance is re-read through a fresh SDK
        /// resolution. <paramref name="diagnostic"/> carries PatternInstanceAmbiguous or
        /// PatternMismatch when resolution was refused, null otherwise.
        /// </summary>
        public KBObject ResolvePatternInstance(KBObject obj, Guid? patternId, bool fresh, out JObject diagnostic)
        {
            diagnostic = null;
            if (obj == null) return null;

            var registry = Registry;
            var requested = ToCandidate(obj);
            var selection = SelectPatternInstance(requested, null, patternId, registry);
            if (selection.Status == PatternInstanceSelectionStatus.Selected) return obj;
            if (selection.Status == PatternInstanceSelectionStatus.Mismatch)
            {
                diagnostic = selection.ToDiagnostic(obj.Name);
                return null;
            }

            // Template-named lookup first (WorkWithPlus<Name> without a patternId): it wins
            // the selection outright, so a hit skips the child walk exactly like the WWP path.
            var namedPattern = registry.FindById(patternId ?? PatternRegistry.WorkWithPlusPatternId);
            string namedInstance = namedPattern?.FormatInstanceName(obj.Name);
            if (!string.IsNullOrWhiteSpace(namedInstance) && _objectService != null)
            {
                var named = fresh
                    ? _objectService.FindObjectFresh(namedInstance, namedPattern.Name)
                    : _objectService.FindObject(namedInstance, namedPattern.Name);
                if (named != null)
                {
                    selection = SelectPatternInstance(requested, new[] { ToCandidate(named) }, patternId, registry);
                    if (selection.Status == PatternInstanceSelectionStatus.Selected) return named;
                }
            }

            var children = new List<PatternInstanceCandidate>();
            try
            {
                var model = obj.Model;
                if (model != null)
                {
                    foreach (KBObject child in model.Objects.GetChildren(obj))
                    {
                        if (child != null) children.Add(ToCandidate(child));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[PatternResolve] child walk failed for " + obj.Name + ": " + ex.Message);
            }

            selection = SelectPatternInstance(requested, children, patternId, registry);
            if (selection.Status != PatternInstanceSelectionStatus.Selected)
            {
                diagnostic = selection.ToDiagnostic(obj.Name);
                return null;
            }

            var selected = selection.Selected.Candidate;
            if (fresh) return _objectService?.FindObjectFresh(selected.Name, selected.TypeName);
            return selected.Source as KBObject;
        }

        public KBObjectPart FindPatternPart(KBObject instanceObj, string partName)
        {
            if (instanceObj == null || string.IsNullOrWhiteSpace(partName)) return null;

            return instanceObj.Parts.Cast<KBObjectPart>().FirstOrDefault(p =>
            {
                if (string.Equals(partName, "PatternInstance", StringComparison.OrdinalIgnoreCase))
                {
                    return p.Name.Equals("PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                           p.GetType().Name.Contains("PatternInstance") ||
                           p.Type.Equals(PatternInstancePartGuid);
                }

                return p.Name.Equals(partName, StringComparison.OrdinalIgnoreCase) ||
                       p.GetType().Name.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       string.Equals(p.TypeDescriptor?.Name, partName, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>Reads a pattern part from the instance of any registered pattern (see <see cref="ResolvePatternInstance"/>).</summary>
        public string ReadPatternPartXml(KBObject obj, string partName, out KBObject resolvedObject, out string resolvedPartName)
        {
            return ReadPatternPartXml(obj, partName, null, out resolvedObject, out resolvedPartName, out _);
        }

        /// <summary>Reads a pattern part restricted to <paramref name="patternId"/> (e.g. WorkWithPlus-only callers).</summary>
        public string ReadPatternPartXml(KBObject obj, string partName, Guid? patternId, out KBObject resolvedObject, out string resolvedPartName)
        {
            return ReadPatternPartXml(obj, partName, patternId, out resolvedObject, out resolvedPartName, out _);
        }

        public string ReadPatternPartXml(KBObject obj, string partName, Guid? patternId, out KBObject resolvedObject, out string resolvedPartName, out JObject diagnostic)
        {
            resolvedObject = ResolvePatternInstance(obj, patternId, fresh: false, out diagnostic);
            return ReadResolvedPatternPart(resolvedObject, partName, out resolvedPartName);
        }

        /// <summary>
        /// Reads a PatternInstance through a fresh SDK resolution for both the
        /// requested object and the resolved pattern instance. Verification
        /// must not invalidate only the parent and then re-use a cached child.
        /// </summary>
        public string ReadPatternPartXmlFresh(KBObject obj, string partName, out KBObject resolvedObject, out string resolvedPartName)
        {
            return ReadPatternPartXmlFresh(obj, partName, null, out resolvedObject, out resolvedPartName, out _);
        }

        public string ReadPatternPartXmlFresh(KBObject obj, string partName, Guid? patternId, out KBObject resolvedObject, out string resolvedPartName, out JObject diagnostic)
        {
            resolvedObject = ResolvePatternInstance(obj, patternId, fresh: true, out diagnostic);
            return ReadResolvedPatternPart(resolvedObject, partName, out resolvedPartName);
        }

        private string ReadResolvedPatternPart(KBObject resolvedObject, string partName, out string resolvedPartName)
        {
            resolvedPartName = partName;
            if (resolvedObject == null) return null;

            var part = FindPatternPart(resolvedObject, partName);
            if (part == null) return null;

            resolvedPartName = !string.IsNullOrWhiteSpace(part.Name) ? part.Name : partName;
            return ExtractEditablePatternXml(part, resolvedObject);
        }

        private KBObject ResolveWWPInstanceFresh(KBObject obj)
        {
            if (obj == null) return null;
            if (obj.TypeDescriptor?.Name?.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase) == true)
                return obj;

            string instanceName = "WorkWithPlus" + obj.Name;
            var namedMatch = _objectService?.FindObjectFresh(instanceName, "WorkWithPlus");
            if (namedMatch != null) return namedMatch;

            try
            {
                var childMatch = obj.Model?.Objects.GetChildren(obj)
                    .FirstOrDefault(o => o.TypeDescriptor?.Name?.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase) == true);
                if (childMatch != null)
                    return _objectService?.FindObjectFresh(childMatch.Name, "WorkWithPlus");
            }
            catch
            {
            }

            return null;
        }

        public string BuildPatternPartEnvelope(KBObject obj, string partName, string innerXml, out KBObject resolvedObject, out KBObjectPart resolvedPart)
        {
            return BuildPatternPartEnvelope(obj, partName, innerXml, null, out resolvedObject, out resolvedPart, out _);
        }

        public string BuildPatternPartEnvelope(KBObject obj, string partName, string innerXml, Guid? patternId, out KBObject resolvedObject, out KBObjectPart resolvedPart)
        {
            return BuildPatternPartEnvelope(obj, partName, innerXml, patternId, out resolvedObject, out resolvedPart, out _);
        }

        public string BuildPatternPartEnvelope(KBObject obj, string partName, string innerXml, Guid? patternId, out KBObject resolvedObject, out KBObjectPart resolvedPart, out JObject diagnostic)
        {
            resolvedObject = ResolvePatternInstance(obj, patternId, fresh: false, out diagnostic);
            resolvedPart = null;
            if (resolvedObject == null) return null;

            resolvedPart = FindPatternPart(resolvedObject, partName);
            if (resolvedPart == null) return null;

            string partXml = SerializeEditablePatternEnvelope(resolvedObject, resolvedPart);
            if (string.IsNullOrWhiteSpace(partXml)) return null;

            try
            {
                var outer = XDocument.Parse(partXml, LoadOptions.PreserveWhitespace);
                var dataElement = outer.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                if (dataElement == null) return null;

                dataElement.ReplaceNodes(new XCData(innerXml));
                return outer.ToString(SaveOptions.DisableFormatting);
            }
            catch
            {
                return null;
            }
        }

        private KBObject FindWWPInstance(Transaction trn)
        {
            var model = trn.Model;
            
            // Search by name using SDK compliant way
            // In GX SDK, names are resolved via ResolveName or looking into the collection
            var instance = model.Objects.GetAll()
                                .FirstOrDefault(o => o.Name.Equals("WorkWithPlus" + trn.Name, StringComparison.OrdinalIgnoreCase));
            
            if (instance != null) return instance;

            // Search by children
            return model.Objects.GetChildren(trn)
                        .FirstOrDefault(o => o.TypeDescriptor.Name.Equals("WorkWithPlus", StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractEditablePatternXml(KBObjectPart part, KBObject instanceObj)
        {
            string xml = TryReadPatternInnerXml(part);
            if (!string.IsNullOrEmpty(xml) && !LooksLikePartPropertiesOnly(xml)) return xml;

            try
            {
                return ExtractInnerXmlFromSerializedFragment(SerializeEditablePatternEnvelope(instanceObj, part));
            }
            catch
            {
                return null;
            }
        }

        private string TryReadPatternInnerXml(KBObjectPart part)
        {
            if (part == null) return null;

            if (part is ISource sourcePart && !string.IsNullOrWhiteSpace(sourcePart.Source))
            {
                return NormalizeXml(sourcePart.Source);
            }

            try
            {
                dynamic dPart = part;
                string[] propertyNames = { "InstanceXml", "Specification", "Settings" };
                foreach (string propertyName in propertyNames)
                {
                    try
                    {
                        string candidate = dPart.Properties.Get<string>(propertyName);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            return NormalizeXml(candidate);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return ExtractInnerXmlFromSerializedFragment(SerializePatternPart(part));
        }

        public string ExtractEditablePatternXmlForDiagnostics(KBObjectPart part)
        {
            return ExtractInnerXmlFromSerializedFragment(SerializePatternPart(part));
        }

        private string SerializePatternPart(KBObjectPart part)
        {
            try
            {
                return part.SerializeToXml();
            }
            catch
            {
                return null;
            }
        }

        private string SerializeEditablePatternEnvelope(KBObject instanceObj, KBObjectPart part)
        {
            if (instanceObj == null || part == null) return null;

            try
            {
                using (var writer = new System.IO.StringWriter())
                {
                    instanceObj.Serialize(writer);
                    string objectXml = writer.ToString();
                    var doc = XDocument.Parse(objectXml, LoadOptions.PreserveWhitespace);
                    var partElement = doc.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName.Equals("Part", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)e.Attribute("type"), part.Type.ToString(), StringComparison.OrdinalIgnoreCase));
                    return partElement?.ToString(SaveOptions.DisableFormatting);
                }
            }
            catch
            {
                return SerializePatternPart(part);
            }
        }

        private string ExtractInnerXmlFromSerializedFragment(string serializedXml)
        {
            if (string.IsNullOrWhiteSpace(serializedXml)) return null;

            try
            {
                var outer = XDocument.Parse(serializedXml, LoadOptions.PreserveWhitespace);
                var dataElement = outer.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
                if (dataElement != null && !string.IsNullOrWhiteSpace(dataElement.Value))
                {
                    return NormalizeXml(dataElement.Value);
                }
            }
            catch
            {
            }

            return NormalizeXml(serializedXml);
        }

        private string NormalizeXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return xml;

            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace).ToString();
            }
            catch
            {
                return xml;
            }
        }

        /// <summary>
        /// True when a serialized pattern part carries only its <c>&lt;Properties&gt;</c> bag
        /// (bare, or wrapped in <c>&lt;Part&gt;</c> without a non-empty <c>&lt;Data&gt;</c>), i.e.
        /// not the pattern instance XML.
        /// </summary>
        internal static bool IsPropertiesOnlyFragment(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;
            if (LooksLikePartPropertiesOnly(xml)) return true;

            try
            {
                var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root;
                if (root == null || !root.Name.LocalName.Equals("Part", StringComparison.OrdinalIgnoreCase)) return false;
                bool hasData = root.Descendants().Any(e =>
                    e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.Value));
                var children = root.Elements().ToList();
                return !hasData && children.Count > 0 &&
                       children.All(e => e.Name.LocalName.Equals("Properties", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        internal static bool LooksLikePartPropertiesOnly(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                return doc.Root != null &&
                       doc.Root.Name.LocalName.Equals("Properties", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private JObject ParseWWPXml(string xml)
        {
            var result = new JObject();
            try
            {
                string realXml = xml;
                if (xml.Contains("<![CDATA["))
                {
                    int start = xml.IndexOf("<![CDATA[") + 9;
                    int end = xml.LastIndexOf("]]>");
                    if (end > start)
                    {
                        realXml = xml.Substring(start, end - start);
                    }
                }

                XDocument doc = XDocument.Parse(realXml);
                var root = doc.Root;
                
                result["template"] = root?.Attribute("template")?.Value ?? root?.Attribute("Template")?.Value;
                
                var attributes = new JArray();
                foreach (var att in doc.Descendants().Where(e => e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase)))
                {
                    var aObj = new JObject();
                    string rawAtt = att.Attribute("attribute")?.Value ?? "";
                    // WWP format is often GUID-AttributeName
                    string name = rawAtt.Contains("-") ? rawAtt.Substring(rawAtt.LastIndexOf("-") + 1) : rawAtt;
                    
                    aObj["name"] = name;
                    aObj["description"] = att.Attribute("description")?.Value;
                    aObj["visible"] = att.Attribute("visible")?.Value;
                    aObj["readOnly"] = att.Attribute("readOnly")?.Value;
                    attributes.Add(aObj);
                }
                result["attributes"] = attributes;

                var variables = new JArray();
                foreach (var varNode in doc.Descendants().Where(e => e.Name.LocalName.Equals("variable", StringComparison.OrdinalIgnoreCase)))
                {
                    var vObj = new JObject();
                    vObj["name"] = varNode.Attribute("name")?.Value ?? varNode.Attribute("Name")?.Value;
                    vObj["description"] = varNode.Attribute("description")?.Value;
                    vObj["readOnly"] = varNode.Attribute("readOnly")?.Value;
                    variables.Add(vObj);
                }
                result["variables"] = variables;

                var actions = new JArray();
                foreach (var act in doc.Descendants().Where(e => e.Name.LocalName.Contains("Action")))
                {
                    var actObj = new JObject();
                    actObj["name"] = act.Attribute("name")?.Value ?? act.Attribute("Name")?.Value;
                    actObj["caption"] = act.Attribute("caption")?.Value ?? act.Attribute("Caption")?.Value;
                    actions.Add(actObj);
                }
                result["actions"] = actions;

                var tabs = new JArray();
                foreach (var tab in doc.Descendants().Where(e => e.Name.LocalName.Equals("tab", StringComparison.OrdinalIgnoreCase)))
                {
                    var tObj = new JObject();
                    tObj["name"] = tab.Attribute("Name")?.Value ?? tab.Attribute("caption")?.Value;
                    tObj["caption"] = tab.Attribute("caption")?.Value;
                    tabs.Add(tObj);
                }
                result["tabs"] = tabs;

                var grids = new JArray();
                foreach (var grid in doc.Descendants().Where(e => e.Name.LocalName.Equals("grid", StringComparison.OrdinalIgnoreCase)))
                {
                    var gObj = new JObject();
                    gObj["name"] = grid.Attribute("name")?.Value ?? grid.Attribute("Name")?.Value;
                    grids.Add(gObj);
                }
                result["grids"] = grids;
            }
            catch (Exception ex)
            {
                result["parsingError"] = ex.Message;
            }
            return result;
        }
    }
}
