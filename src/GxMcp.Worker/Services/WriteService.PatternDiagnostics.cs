using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    // Pattern-write reflection diagnostics (GX_MCP_PATTERN_DEBUG / _NATIVE_EXPERIMENT gated
    // logging helpers) extracted from WriteService.cs (plan 007). Pure move, no logic changes
    // — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        private void LogPatternInMemoryStateIfEnabled(
            global::Artech.Architecture.Common.Objects.KBObject requestedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string partName,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                if (resolvedPart != null)
                {
                    string serializedPart = _patternAnalysisService.ExtractEditablePatternXmlForDiagnostics(resolvedPart);
                    if (!string.IsNullOrWhiteSpace(serializedPart))
                    {
                        string normalizedSerializedPart = XDocument.Parse(serializedPart, LoadOptions.PreserveWhitespace).ToString();
                        Logger.Info("[PATTERN-DEBUG] Resolved part equals requested after apply: " + string.Equals(normalizedSerializedPart, normalizedInput, StringComparison.Ordinal));
                        Logger.Info("[PATTERN-DEBUG] Resolved part hash after apply: " + normalizedSerializedPart.GetHashCode() + "; requested hash=" + normalizedInput.GetHashCode());
                    }
                }

                string currentXml = _patternAnalysisService.ReadPatternPartXml(requestedObject, partName, out _, out _);
                string normalizedCurrent = string.IsNullOrWhiteSpace(currentXml)
                    ? string.Empty
                    : XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace).ToString();
                Logger.Info("[PATTERN-DEBUG] In-memory equals requested after apply: " + string.Equals(normalizedCurrent, normalizedInput, StringComparison.Ordinal));
                Logger.Info("[PATTERN-DEBUG] In-memory hash after apply: " + normalizedCurrent.GetHashCode() + "; requested hash=" + normalizedInput.GetHashCode());
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] In-memory state inspection failed: " + ex.Message);
            }
        }

        private bool TryApplyNativePatternMutationExperiment(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string innerXml)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_NATIVE_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (resolvedPart == null || string.IsNullOrWhiteSpace(innerXml)) return false;

            try
            {
                var requestedValues = ExtractRequestedGridVariableValues(innerXml);
                if (requestedValues.Count == 0)
                {
                    Logger.Info("[PATTERN-DEBUG] Native mutation experiment skipped: no target gridVariable changes found in requested XML.");
                    return false;
                }

                object rootElement = GetReadablePropertyValue(resolvedPart, "RootElement");
                if (rootElement == null)
                {
                    Logger.Warn("[PATTERN-DEBUG] Native mutation experiment skipped: RootElement not available.");
                    return false;
                }

                var targetElements = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                CollectPatternElementsByName(rootElement, targetElements);

                bool changed = false;
                foreach (var entry in requestedValues)
                {
                    if (!targetElements.TryGetValue(entry.Key, out object element) || element == null)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Native mutation experiment could not locate gridVariable '" + entry.Key + "'.");
                        continue;
                    }

                    object attributes = GetReadablePropertyValue(element, "Attributes");
                    if (attributes == null)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Native mutation experiment found '" + entry.Key + "' without Attributes.");
                        continue;
                    }

                    foreach (var attributeValue in entry.Value)
                    {
                        bool applied = TryApplyPatternDeltaCommand(element, attributes, attributeValue.Key, attributeValue.Value);
                        if (!applied)
                        {
                            applied = TrySetPatternAttributeValue(attributes, attributeValue.Key, attributeValue.Value);
                        }

                        if (applied)
                        {
                            changed = true;
                            Logger.Info("[PATTERN-DEBUG] Native mutation applied: " + entry.Key + "." + attributeValue.Key + "=" + attributeValue.Value);
                        }
                        else
                        {
                            Logger.Warn("[PATTERN-DEBUG] Native mutation failed for " + entry.Key + "." + attributeValue.Key + "=" + attributeValue.Value);
                        }
                    }
                }

                return changed;
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Native mutation experiment failed: " + ex.Message);
                return false;
            }
        }

        private Dictionary<string, Dictionary<string, string>> ExtractRequestedGridVariableValues(string innerXml)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var document = XDocument.Parse(innerXml, LoadOptions.PreserveWhitespace);
                var seenOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in document
                    .Descendants()
                    .Where(e => e.Name.LocalName.Equals("gridVariable", StringComparison.OrdinalIgnoreCase)))
                {
                    string name = (string)element.Attribute("name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!string.Equals(name, "HorasDebito", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "SedCPHor", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddRequestedAttribute(values, element, "description");
                    AddRequestedAttribute(values, element, "defaultDescription");
                    AddRequestedAttribute(values, element, "visible");
                    AddRequestedAttribute(values, element, "defaultVisible");

                    int occurrence = seenOccurrences.TryGetValue(name, out int current) ? current + 1 : 1;
                    seenOccurrences[name] = occurrence;
                    LogRequestedGridVariableOccurrence(name, occurrence, element, values);

                    if (values.Count > 0)
                    {
                        result[name] = values;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Failed to parse requested PatternInstance XML for native mutation: " + ex.Message);
            }

            return result;
        }

        private void LogRequestedPatternPayloadIfEnabled(string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(normalizedInput)) return;

            try
            {
                foreach (string name in new[] { "HorasDebito", "SedCPHor" })
                {
                    string snippet = ExtractGridVariableSnippet(normalizedInput, name);
                    if (string.IsNullOrWhiteSpace(snippet))
                    {
                        Logger.Info("[PATTERN-DEBUG] REQUESTED-PAYLOAD name=" + name + " snippet=<missing>");
                        continue;
                    }

                    Logger.Info("[PATTERN-DEBUG] REQUESTED-PAYLOAD name=" + name + " hash=" + snippet.GetHashCode() + " snippet=" + snippet);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] REQUESTED-PAYLOAD logging failed: " + ex.Message);
            }
        }

        private string ExtractGridVariableSnippet(string xml, string name)
        {
            if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(name)) return null;

            string marker = "name=\"" + name + "\"";
            int markerIndex = xml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;

            int start = xml.LastIndexOf("<gridVariable", markerIndex, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            int end = xml.IndexOf("/>", markerIndex, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;

            end += 2;
            if (end <= start || end > xml.Length) return null;

            return xml.Substring(start, end - start);
        }

        private void LogRequestedGridVariableOccurrence(string name, int occurrence, XElement element, Dictionary<string, string> values)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                string path = BuildRequestedElementPath(element);
                string attrs = string.Join(", ", values.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => kvp.Key + "=" + kvp.Value));
                Logger.Info("[PATTERN-DEBUG] REQUESTED-GRID occurrence=" + occurrence + " name=" + name + " path=" + path + " values=[" + attrs + "]");
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] REQUESTED-GRID logging failed for " + name + ": " + ex.Message);
            }
        }

        private string BuildRequestedElementPath(XElement element)
        {
            if (element == null) return "<null>";

            var segments = new Stack<string>();
            XElement current = element;
            while (current != null)
            {
                string name = current.Name.LocalName;
                string identifier = (string)current.Attribute("name");
                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    segments.Push(name + "[" + identifier + "]");
                }
                else
                {
                    int index = current.Parent == null
                        ? 1
                        : current.Parent.Elements(current.Name).TakeWhile(e => e != current).Count() + 1;
                    segments.Push(name + "[" + index + "]");
                }

                current = current.Parent;
            }

            return "/" + string.Join("/", segments);
        }

        private void AddRequestedAttribute(Dictionary<string, string> values, XElement element, string attributeName)
        {
            string value = (string)element.Attribute(attributeName);
            if (value != null)
            {
                values[attributeName] = value;
            }
        }

        private void CollectPatternElementsByName(object element, Dictionary<string, object> matches)
        {
            if (element == null) return;

            Type type = element.GetType();
            string elementType = ReadStringProperty(type, element, "Name");
            string keyValue = ReadStringProperty(type, element, "KeyValueString");
            string propertyTitle = ReadStringProperty(type, element, "PropertyTitle");
            string path = ReadStringProperty(type, element, "Path");
            object attributes = GetReadablePropertyValue(element, "Attributes");
            string attributeName = TryGetPatternAttributeValue(attributes, "name");

            if (string.Equals(elementType, "gridVariable", StringComparison.OrdinalIgnoreCase))
            {
                string candidateName = FirstNonEmpty(attributeName, keyValue, propertyTitle, ExtractNameFromPath(path));
                if (!string.IsNullOrWhiteSpace(candidateName) && !matches.ContainsKey(candidateName))
                {
                    matches[candidateName] = element;
                }
            }

            object children = GetReadablePropertyValue(element, "Children");
            if (!(children is System.Collections.IEnumerable enumerable)) return;

            foreach (object child in enumerable)
            {
                if (child == null) continue;
                CollectPatternElementsByName(child, matches);
            }
        }

        private string ExtractNameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            int atIndex = path.LastIndexOf("[@name=\"", StringComparison.OrdinalIgnoreCase);
            if (atIndex >= 0)
            {
                int start = atIndex + 8;
                int end = path.IndexOf("\"]", start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    return path.Substring(start, end - start);
                }
            }

            int bracketIndex = path.LastIndexOf('[', path.Length - 1);
            int closeIndex = path.LastIndexOf(']');
            if (bracketIndex >= 0 && closeIndex > bracketIndex)
            {
                string token = path.Substring(bracketIndex + 1, closeIndex - bracketIndex - 1);
                if (!int.TryParse(token, out _) && token.IndexOf('"') < 0)
                {
                    return token;
                }
            }

            return null;
        }

        private string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private object GetReadablePropertyValue(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length > 0) return null;
                return property.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        private bool TrySetPatternAttributeValue(object attributes, string propertyName, string value)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(propertyName)) return false;

            Type type = attributes.GetType();
            string before = TryGetPatternAttributeValue(attributes, propertyName);

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, "SetPropertyValueString", StringComparison.OrdinalIgnoreCase)))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string))
                {
                    try
                    {
                        method.Invoke(attributes, new object[] { propertyName, value });
                        string after = TryGetPatternAttributeValue(attributes, propertyName);
                        if (string.Equals(after, value, StringComparison.Ordinal))
                        {
                            return !string.Equals(before, after, StringComparison.Ordinal);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            MethodInfo setPropertyValue = type.GetMethod("SetPropertyValue", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(object) }, null);
            if (setPropertyValue != null)
            {
                try
                {
                    setPropertyValue.Invoke(attributes, new object[] { propertyName, value });
                    string after = TryGetPatternAttributeValue(attributes, propertyName);
                    if (string.Equals(after, value, StringComparison.Ordinal))
                    {
                        return !string.Equals(before, after, StringComparison.Ordinal);
                    }
                }
                catch
                {
                }
            }

            var indexer = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, typeof(object), new[] { typeof(string) }, null);
            if (indexer != null)
            {
                try
                {
                    indexer.SetValue(attributes, value, new object[] { propertyName });
                    string after = TryGetPatternAttributeValue(attributes, propertyName);
                    if (string.Equals(after, value, StringComparison.Ordinal))
                    {
                        return !string.Equals(before, after, StringComparison.Ordinal);
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private bool TryApplyPatternDeltaCommand(object element, object attributes, string propertyName, string value)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DELTA_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (element == null || attributes == null || string.IsNullOrWhiteSpace(propertyName)) return false;

            try
            {
                string before = TryGetPatternAttributeValue(attributes, propertyName);
                if (string.Equals(before, value, StringComparison.Ordinal))
                {
                    return false;
                }

                Type commandType = element.GetType().Assembly.GetType("Artech.Packages.Patterns.Objects.ChangeAttributeValueCommand", false, true);
                if (commandType == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Delta command type not found.");
                    return false;
                }

                ConstructorInfo ctor = commandType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var parameters = c.GetParameters();
                        return parameters.Length == 4 &&
                               parameters[0].ParameterType.IsInstanceOfType(element) &&
                               parameters[1].ParameterType == typeof(string);
                    });
                if (ctor == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Delta command ctor not found for " + propertyName + ".");
                    return false;
                }

                object command = ctor.Invoke(new object[] { element, propertyName, before, value });
                MethodInfo isSafe = commandType.GetMethod("IsSafeToExecute", BindingFlags.Public | BindingFlags.Instance);
                if (isSafe != null)
                {
                    object safeResult = isSafe.Invoke(command, null);
                    Logger.Info("[PATTERN-DEBUG] Delta command IsSafeToExecute for " + propertyName + " => " + DescribeValue(safeResult));
                    if (safeResult is bool safe && !safe)
                    {
                        return false;
                    }
                }

                MethodInfo execute = commandType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Delta command Execute missing for " + propertyName + ".");
                    return false;
                }

                execute.Invoke(command, null);
                string after = TryGetPatternAttributeValue(attributes, propertyName);
                Logger.Info("[PATTERN-DEBUG] Delta command applied " + propertyName + ": before=" + before + "; after=" + after + "; target=" + value);
                return string.Equals(after, value, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Delta command failed for " + propertyName + ": " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private string TryGetPatternAttributeValue(object attributes, string propertyName)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            Type type = attributes.GetType();

            MethodInfo getter = type.GetMethod("GetPropertyValueString", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (getter != null)
            {
                try
                {
                    return getter.Invoke(attributes, new object[] { propertyName })?.ToString();
                }
                catch
                {
                }
            }

            var indexer = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, typeof(object), new[] { typeof(string) }, null);
            if (indexer != null)
            {
                try
                {
                    return indexer.GetValue(attributes, new object[] { propertyName })?.ToString();
                }
                catch
                {
                }
            }

            return null;
        }

        private void LogPatternDiagnosticsIfEnabled(
            global::Artech.Architecture.Common.Objects.KBObject requestedObject,
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                var partType = resolvedPart.GetType();
                string executeUpdateSignature = TryGetMethodSignature(partType, "ExecuteUpdate");
                string deserializeSignature = string.Join(" | ", partType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "DeserializeFromXml", StringComparison.OrdinalIgnoreCase))
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());

                string serializeSignature = TryGetMethodSignature(partType, "SerializeToXml");

                Logger.Info("[PATTERN-DEBUG] Requested object: " + requestedObject.Name + " (" + requestedObject.TypeDescriptor?.Name + ")");
                Logger.Info("[PATTERN-DEBUG] Resolved object: " + resolvedObject.Name + " (" + resolvedObject.TypeDescriptor?.Name + ")");
                LogResolvedObjectDiagnostics(resolvedObject);
                Logger.Info("[PATTERN-DEBUG] Part type: " + partType.FullName);
                Logger.Info("[PATTERN-DEBUG] Part name/type descriptor: " + (resolvedPart.Name ?? "<null>") + " / " + (resolvedPart.TypeDescriptor?.Name ?? "<null>"));
                Logger.Info("[PATTERN-DEBUG] ExecuteUpdate: " + executeUpdateSignature);
                Logger.Info("[PATTERN-DEBUG] DeserializeFromXml overloads: " + deserializeSignature);
                Logger.Info("[PATTERN-DEBUG] SerializeToXml: " + serializeSignature);
                Logger.Info("[PATTERN-DEBUG] Type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(partType)));
                Logger.Info("[PATTERN-DEBUG] Interesting properties: " + string.Join(" | ", GetInterestingPropertySignatures(partType)));
                Logger.Info("[PATTERN-DEBUG] Interesting fields: " + string.Join(" | ", GetInterestingFieldSignatures(partType)));
                Logger.Info("[PATTERN-DEBUG] Interesting methods: " + string.Join(" | ", GetInterestingMethodSignatures(partType)));
                LogInterestingPropertyValues(resolvedPart, partType, resolvedObject);
                TryLogMethodResult("Part", resolvedPart, partType, "GetDataUpdateProcess");
                TryLogMethodResult("Part", resolvedPart, partType, "GetDataVersionAdapter");
                TryLogSemanticWorkWithPlusInstance(resolvedObject);
                Logger.Info("[PATTERN-DEBUG] Input hash: " + normalizedInput.GetHashCode() + "; length=" + normalizedInput.Length);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Diagnostic logging failed: " + ex.Message);
            }
        }

        private void RunPatternPreSaveExperimentIfEnabled(
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_PRESAVE_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;
            if (resolvedObject == null) return;

            try
            {
                LogPatternValidationState("pre-save baseline", resolvedObject);
                LogPatternStateMethods(resolvedObject);
                LogNamedInterfaceProperties("pre-save baseline", resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");
                RunPatternPartHooks("pre-save baseline", resolvedObject, resolvedPart);
                TryPatternSemanticGridSaveExperiment(resolvedObject, resolvedPart, normalizedInput);

                TryInvokeInterfaceMethodByName(resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget", "PreserveDefaultLock", Array.Empty<object>());
                LogPatternValidationState("after PreserveDefaultLock", resolvedObject);
                LogNamedInterfaceProperties("after PreserveDefaultLock", resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");

                string[] noArgMethods =
                {
                    "LoadInstancePropertyDefinition",
                    "RefreshDefaultDependentParts",
                    "CalculateDefault",
                    "CanCalculateDefault",
                    "ShouldRegenerate"
                };

                foreach (string methodName in noArgMethods)
                {
                    TryInvokePatternMethod(resolvedObject, methodName, Array.Empty<object>());
                    LogPatternValidationState("after " + methodName, resolvedObject);
                    LogNamedInterfaceProperties("after " + methodName, resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");
                    RunPatternPartHooks("after " + methodName, resolvedObject, resolvedPart);
                }

                TryInvokeInterfaceMethodByName(resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget", "PreserveDefaultUnlock", Array.Empty<object>());
                LogPatternValidationState("after PreserveDefaultUnlock", resolvedObject);
                LogNamedInterfaceProperties("after PreserveDefaultUnlock", resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");

                TryInvokePatternMethod(resolvedObject, "SaveUpdates", new object[] { false });
                LogPatternValidationState("after SaveUpdates(false)", resolvedObject);
                RunPatternPartHooks("after SaveUpdates(false)", resolvedObject, resolvedPart);

                TryInvokePatternMethod(resolvedObject, "SaveUpdates", new object[] { true });
                LogPatternValidationState("after SaveUpdates(true)", resolvedObject);
                RunPatternPartHooks("after SaveUpdates(true)", resolvedObject, resolvedPart);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Pre-save experiment failed: " + ex.Message);
            }
        }

        private bool TryPatternSemanticGridSaveExperiment(
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_SEMANTIC_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (resolvedObject == null || resolvedPart == null || string.IsNullOrWhiteSpace(normalizedInput)) return false;

            try
            {
                var requestedValues = ExtractRequestedGridVariableValues(normalizedInput);
                var targetNames = new[] { "HorasDebito", "SedCPHor" };
                var requestedTargets = targetNames
                    .Where(name => requestedValues.TryGetValue(name, out var values) && values.Count > 0)
                    .Select(name => new
                    {
                        Name = name,
                        Values = requestedValues[name]
                    })
                    .ToList();

                if (requestedTargets.Count == 0)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: no HorasDebito or SedCPHor changes requested.");
                    return false;
                }

                Type semanticInstanceType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly => assembly.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPlusInstance", false, true))
                    .FirstOrDefault(type => type != null);
                if (semanticInstanceType == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: WorkWithPlusInstance type not loaded.");
                    return false;
                }

                ConstructorInfo ctor = semanticInstanceType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var parameters = c.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(resolvedObject);
                    });
                if (ctor == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: constructor not compatible.");
                    return false;
                }

                object semanticInstance = ctor.Invoke(new object[] { resolvedObject });
                object settings = GetReadablePropertyValue(semanticInstance, "Settings");
                if (settings == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: Settings not available.");
                    return false;
                }

                object gridSettings = null;
                MethodInfo getAllChildren = settings.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren != null)
                {
                    var allChildren = getAllChildren.Invoke(settings, null) as System.Collections.IEnumerable;
                    if (allChildren != null)
                    {
                        foreach (object child in EnumerateSemanticItems(allChildren))
                        {
                            if (child == null) continue;
                            if (string.Equals(child.GetType().FullName, "DVelop.Patterns.WorkWithPlus.SettingsGridElement", StringComparison.Ordinal))
                            {
                                gridSettings = child;
                                break;
                            }
                        }
                    }
                }

                if (gridSettings == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: SettingsGridElement not found.");
                    return false;
                }

                object rootElement = GetReadablePropertyValue(resolvedPart, "RootElement");
                if (rootElement == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: RootElement not available.");
                    return false;
                }

                var targetElements = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                CollectPatternElementsByName(rootElement, targetElements);

                MethodInfo saveAttribute = gridSettings.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "SaveAttribute", StringComparison.OrdinalIgnoreCase) &&
                        method.GetParameters().Length == 4);
                if (saveAttribute == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: SaveAttribute unavailable.");
                    return false;
                }

                bool changed = false;
                foreach (var target in requestedTargets)
                {
                    if (!targetElements.TryGetValue(target.Name, out object targetElement) || targetElement == null)
                    {
                        WritePatternDebugTrace("Semantic grid experiment skipped: " + target.Name + " element not found.");
                        continue;
                    }

                    WritePatternDebugTrace("Semantic grid target before " + target.Name + "=" + DescribeSemanticElement(targetElement));

                    foreach (string attName in new[] { "description", "Description", "defaultDescription", "DefaultDescription" })
                    {
                        string attValue;
                        if (!target.Values.TryGetValue(attName, out attValue))
                        {
                            string fallbackKey = attName.StartsWith("default", StringComparison.OrdinalIgnoreCase) ? "defaultDescription" : "description";
                            if (!target.Values.TryGetValue(fallbackKey, out attValue))
                            {
                                continue;
                            }
                        }

                        object result = saveAttribute.Invoke(gridSettings, new object[] { targetElement, attName, attValue, attName.StartsWith("default", StringComparison.OrdinalIgnoreCase) });
                        WritePatternDebugTrace("Semantic grid SaveAttribute " + target.Name + "." + attName + " => " + DescribeValue(result));
                        changed = true;
                    }

                    WritePatternDebugTrace("Semantic grid target after " + target.Name + "=" + DescribeSemanticElement(targetElement));
                }

                if (changed)
                {
                    WritePatternDebugTrace("Semantic grid experiment applied requested changes.");
                }

                return changed;
            }
            catch (Exception ex)
            {
                WritePatternDebugTrace("Semantic grid experiment failed: " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private void RunPatternPartHooks(
            string stage,
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart)
        {
            if (resolvedObject == null || resolvedPart == null) return;

            try
            {
                MethodInfo getValidator = resolvedPart.GetType().GetMethod("GetValidator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getValidator != null)
                {
                    object validator = getValidator.Invoke(resolvedPart, null);
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " GetValidator()=" + DescribeValue(validator));
                    TryRunPatternValidator(stage, validator, resolvedObject);
                }

                MethodInfo getUpdateProcess = resolvedPart.GetType().GetMethod("GetDataUpdateProcess", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getUpdateProcess != null)
                {
                    object updateProcess = getUpdateProcess.Invoke(resolvedPart, null);
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " GetDataUpdateProcess()=" + DescribeValue(updateProcess));
                    TryRunPatternUpdateProcess(stage, updateProcess, resolvedObject);
                }

                TryLogPatternDefinitionHooks(stage, resolvedPart);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hooks failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryRunPatternValidator(string stage, object validator, object patternObject)
        {
            if (validator == null || patternObject == null) return;

            try
            {
                MethodInfo validateMethod = validator.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Validate", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 2);
                if (validateMethod == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " validator has no compatible Validate method.");
                    return;
                }

                var output = new Artech.Common.Diagnostics.OutputMessages();
                object result = validateMethod.Invoke(validator, new object[] { patternObject, output });
                string summary = output.ErrorText;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = output.FullText;
                }

                Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " validator.Validate => " + DescribeValue(result) + "; hasErrors=" + output.HasErrors + "; messages=" + (summary ?? string.Empty));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hook validator failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryRunPatternUpdateProcess(string stage, object updateProcess, object patternObject)
        {
            if (updateProcess == null || patternObject == null) return;

            try
            {
                MethodInfo updateMethod = updateProcess.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "UpdateObject", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 1);
                if (updateMethod == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " update process has no compatible UpdateObject method.");
                    return;
                }

                object result = updateMethod.Invoke(updateProcess, new object[] { patternObject });
                Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " updateProcess.UpdateObject => " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hook update process failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogPatternDefinitionHooks(string stage, object resolvedPart)
        {
            if (resolvedPart == null) return;

            try
            {
                Type iface = resolvedPart.GetType().GetInterfaces()
                    .FirstOrDefault(i => string.Equals(i.FullName, "Artech.Packages.Patterns.Engine.IPatternXPathNavigable", StringComparison.OrdinalIgnoreCase));
                if (iface == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " IPatternXPathNavigable not implemented.");
                    return;
                }

                PropertyInfo patternProp = iface.GetProperty("Pattern", BindingFlags.Public | BindingFlags.Instance);
                object pattern = patternProp?.GetValue(resolvedPart, null);
                Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " Pattern=" + DescribeValue(pattern));
                if (pattern == null) return;

                foreach (string methodName in new[] { "GetInstanceValidator", "GetInstanceUpdateProcess", "GetInstanceVersionAdapter" })
                {
                    try
                    {
                        MethodInfo method = pattern.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                        if (method == null)
                        {
                            Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " Pattern." + methodName + "()=<missing>");
                            continue;
                        }

                        object result = method.Invoke(pattern, null);
                        Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " Pattern." + methodName + "()=" + DescribeValue(result));
                    }
                    catch (Exception exMethod)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Part hook " + stage + " Pattern." + methodName + "() failed: " + (exMethod.InnerException?.Message ?? exMethod.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hook pattern definition failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private bool TryPatternDirectSaveExperiment(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DIRECT_SAVE_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (resolvedObject == null) return false;

            try
            {
                MethodInfo saveMethod = resolvedObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Save", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (saveMethod != null)
                {
                    object saveResult = saveMethod.Invoke(resolvedObject, Array.Empty<object>());
                    Logger.Info("[PATTERN-DEBUG] Direct save experiment invoked " + FormatMethodSignature(saveMethod) + " => " + DescribeValue(saveResult));
                    return true;
                }

                MethodInfo saveWithPreferences = resolvedObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Save", StringComparison.OrdinalIgnoreCase) &&
                                         m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.Name.IndexOf("SavePreferences", StringComparison.OrdinalIgnoreCase) >= 0);

                if (saveWithPreferences != null)
                {
                    object preferences = null;
                    try
                    {
                        preferences = Activator.CreateInstance(saveWithPreferences.GetParameters()[0].ParameterType);
                    }
                    catch (Exception exCtor)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Direct save experiment could not create save preferences: " + exCtor.Message);
                    }

                    if (preferences != null)
                    {
                        object saveResult = saveWithPreferences.Invoke(resolvedObject, new[] { preferences });
                        Logger.Info("[PATTERN-DEBUG] Direct save experiment invoked " + FormatMethodSignature(saveWithPreferences) + " => " + DescribeValue(saveResult));
                        return true;
                    }
                }

                Logger.Warn("[PATTERN-DEBUG] Direct save experiment could not locate a parameterless Save method.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Direct save experiment failed: " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private void TryInvokePatternMethod(object target, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName)) return;

            try
            {
                MethodInfo method = FindCompatibleMethod(target.GetType(), methodName, args);
                if (method == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Pre-save method not found: " + methodName);
                    return;
                }

                object result = method.Invoke(target, args);
                Logger.Info("[PATTERN-DEBUG] Pre-save invoked " + FormatMethodSignature(method) + " => " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Pre-save method failed " + methodName + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private MethodInfo FindCompatibleMethod(Type type, string methodName, object[] args)
        {
            if (type == null || string.IsNullOrWhiteSpace(methodName)) return null;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) ||
                            m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length) continue;

                bool compatible = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (args[i] == null) continue;
                    if (!parameters[i].ParameterType.IsInstanceOfType(args[i]) &&
                        !(parameters[i].ParameterType.IsValueType && parameters[i].ParameterType == args[i].GetType()))
                    {
                        compatible = false;
                        break;
                    }
                }

                if (compatible) return method;
            }

            return null;
        }

        private void LogPatternValidationState(string stage, global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            try
            {
                var output = new Artech.Common.Diagnostics.OutputMessages();
                bool isValid = resolvedObject.Validate(output);
                string summary = output.ErrorText;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = output.FullText;
                }
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = resolvedObject.GetSdkMessages();
                }

                Logger.Info("[PATTERN-DEBUG] Validation state " + stage + ": isValid=" + isValid + "; hasErrors=" + output.HasErrors + "; messages=" + (summary ?? string.Empty));
                LogPatternValidationFlags(stage, resolvedObject);

                MethodInfo validateStateMethod = FindCompatibleMethod(resolvedObject.GetType(), "ValidateState", new object[] { output });
                if (validateStateMethod != null)
                {
                    try
                    {
                        var stateOutput = new Artech.Common.Diagnostics.OutputMessages();
                        object stateResult = validateStateMethod.Invoke(resolvedObject, new object[] { stateOutput });
                        string stateSummary = stateOutput.ErrorText;
                        if (string.IsNullOrWhiteSpace(stateSummary))
                        {
                            stateSummary = stateOutput.FullText;
                        }
                        Logger.Info("[PATTERN-DEBUG] ValidateState " + stage + ": result=" + DescribeValue(stateResult) + "; hasErrors=" + stateOutput.HasErrors + "; messages=" + (stateSummary ?? string.Empty));
                    }
                    catch (Exception exState)
                    {
                        Logger.Warn("[PATTERN-DEBUG] ValidateState failed at " + stage + ": " + (exState.InnerException?.Message ?? exState.Message));
                    }
                }

                MethodInfo validateDataMethod = FindCompatibleMethod(resolvedObject.GetType(), "ValidateData", new object[] { output });
                if (validateDataMethod != null)
                {
                    try
                    {
                        var dataOutput = new Artech.Common.Diagnostics.OutputMessages();
                        object dataResult = validateDataMethod.Invoke(resolvedObject, new object[] { dataOutput });
                        string dataSummary = dataOutput.ErrorText;
                        if (string.IsNullOrWhiteSpace(dataSummary))
                        {
                            dataSummary = dataOutput.FullText;
                        }
                        Logger.Info("[PATTERN-DEBUG] ValidateData " + stage + ": result=" + DescribeValue(dataResult) + "; hasErrors=" + dataOutput.HasErrors + "; messages=" + (dataSummary ?? string.Empty));
                    }
                    catch (Exception exData)
                    {
                        Logger.Warn("[PATTERN-DEBUG] ValidateData failed at " + stage + ": " + (exData.InnerException?.Message ?? exData.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Validation state logging failed at " + stage + ": " + ex.Message);
            }
        }

        private void LogPatternValidationFlags(string stage, object target)
        {
            if (target == null) return;

            try
            {
                Type type = target.GetType();
                string[] interfaces = type.GetInterfaces()
                    .Select(i => i.FullName ?? i.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .ToArray();

                Logger.Info("[PATTERN-DEBUG] Interfaces " + stage + ": " + string.Join(" | ", interfaces));

                var candidates = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Where(p => p.PropertyType == typeof(bool) ||
                                p.PropertyType == typeof(bool?) ||
                                p.PropertyType == typeof(string) ||
                                p.PropertyType.IsEnum)
                    .Where(p => MatchesValidationSignal(p.Name))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToArray();

                foreach (var prop in candidates)
                {
                    try
                    {
                        object value = prop.GetValue(target, null);
                        Logger.Info("[PATTERN-DEBUG] Flag " + stage + " " + prop.Name + "=" + DescribeValue(value));
                    }
                    catch (Exception exProp)
                    {
                        Logger.Info("[PATTERN-DEBUG] Flag " + stage + " " + prop.Name + "=<error " + exProp.GetType().Name + ": " + exProp.Message + ">");
                    }
                }

                LogNamedInterfaceProperties(stage, target, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Validation flag logging failed at " + stage + ": " + ex.Message);
            }
        }

        private bool MatchesValidationSignal(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            string[] tokens =
            {
                "valid",
                "invalid",
                "dirty",
                "modified",
                "change",
                "generate",
                "regenerate",
                "update",
                "save",
                "default",
                "error",
                "open",
                "load",
                "state",
                "sync"
            };

            return tokens.Any(token => name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LogPatternStateMethods(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            if (resolvedObject == null) return;

            string[] propertyNames = { "LastInstanceGeneration", "LastInstanceUpdate", "LastInstanceCalculateDefault", "SaveOutput" };
            foreach (string propertyName in propertyNames)
            {
                object value = GetReadablePropertyValue(resolvedObject, propertyName);
                Logger.Info("[PATTERN-DEBUG] State property " + propertyName + "=" + DescribeValue(value));
            }
        }

        private void LogResolvedObjectDiagnostics(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            if (resolvedObject == null) return;

            try
            {
                Type objectType = resolvedObject.GetType();
                Logger.Info("[PATTERN-DEBUG] Resolved object type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(objectType)));
                Logger.Info("[PATTERN-DEBUG] Resolved object ctors: " + string.Join(" | ", objectType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(ctor => FormatConstructorSignature(ctor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
                Logger.Info("[PATTERN-DEBUG] Resolved object interesting properties: " + string.Join(" | ", GetInterestingPropertySignatures(objectType)));
                Logger.Info("[PATTERN-DEBUG] Resolved object interesting methods: " + string.Join(" | ", GetInterestingMethodSignatures(objectType)));

                string[] interestingMethodNames =
                {
                    "Validate",
                    "Save",
                    "Apply",
                    "Generate",
                    "Regenerate",
                    "Update",
                    "Synchronize",
                    "Refresh",
                    "CalculateDefault",
                    "LoadInstancePropertyDefinition"
                };

                foreach (string methodName in interestingMethodNames)
                {
                    var matches = objectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(m => m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(FormatMethodSignature)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(12)
                        .ToArray();

                    if (matches.Length > 0)
                    {
                        Logger.Info("[PATTERN-DEBUG] Resolved object methods matching '" + methodName + "': " + string.Join(" | ", matches));
                    }
                }

                TryLogPatternDefinitionObject(resolvedObject);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Resolved object diagnostic logging failed: " + ex.Message);
            }
        }

        private string TryGetMethodSignature(Type type, string methodName)
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
            return method == null ? "<missing>" : FormatMethodSignature(method);
        }

        private string FormatMethodSignature(MethodInfo method)
        {
            string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
            return method.ReturnType.Name + " " + method.Name + "(" + parameters + ")";
        }

        private string FormatConstructorSignature(ConstructorInfo ctor)
        {
            string parameters = string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
            return "CTOR(" + parameters + ")";
        }

        private IEnumerable<string> GetTypeHierarchy(Type type)
        {
            var current = type;
            while (current != null)
            {
                yield return current.FullName ?? current.Name;
                current = current.BaseType;
            }
        }

        private IEnumerable<string> GetInterestingPropertySignatures(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => IsInterestingMemberName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
        }

        private IEnumerable<string> GetInterestingFieldSignatures(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => IsInterestingMemberName(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => (f.FieldType?.Name ?? "<unknown>") + " " + f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
        }

        private IEnumerable<string> GetInterestingMethodSignatures(Type type)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => IsInterestingMemberName(m.Name))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(FormatMethodSignature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToArray();
        }

        private bool IsInterestingMemberName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            string[] tokens =
            {
                "pattern",
                "instance",
                "grid",
                "variable",
                "node",
                "item",
                "property",
                "model",
                "root",
                "xml",
                "data",
                "attribute",
                "control"
                ,"children"
            };

            return tokens.Any(token => name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LogInterestingPropertyValues(
            object instance,
            Type type,
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => IsInterestingMemberName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object value = prop.GetValue(instance, null);
                    Logger.Info("[PATTERN-DEBUG] Property value " + prop.Name + ": " + DescribeValue(value));
                    if (value != null && (string.Equals(prop.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "RootElement", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Attributes", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Children", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Objects", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Parent", StringComparison.OrdinalIgnoreCase)))
                    {
                        LogNestedPatternObjectDiagnostics(prop.Name, value, resolvedObject);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info("[PATTERN-DEBUG] Property value " + prop.Name + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
                }
            }
        }

        private string DescribeValue(object value)
        {
            if (value == null) return "<null>";

            if (value is string text)
            {
                string compact = text.Replace("\r", "\\r").Replace("\n", "\\n");
                return "String(len=" + text.Length + "): " + (compact.Length > 160 ? compact.Substring(0, 160) + "..." : compact);
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var sb = new StringBuilder();
                int count = 0;
                foreach (object item in enumerable)
                {
                    if (count >= 5) break;
                    if (count > 0) sb.Append(", ");
                    sb.Append(item == null ? "<null>" : item.GetType().FullName ?? item.GetType().Name);
                    count++;
                }
                return (value.GetType().FullName ?? value.GetType().Name) + "(sampleCount=" + count + (count > 0 ? "; items=" + sb : string.Empty) + ")";
            }

            return (value.GetType().FullName ?? value.GetType().Name) + ": " + value;
        }

        private void LogNestedPatternObjectDiagnostics(string label, object value, global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            var nestedType = value.GetType();
            Logger.Info("[PATTERN-DEBUG] " + label + " type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " properties: " + string.Join(" | ", GetInterestingPropertySignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " fields: " + string.Join(" | ", GetInterestingFieldSignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " methods: " + string.Join(" | ", GetInterestingMethodSignatures(nestedType)));
            if (string.Equals(label, "RootElement", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("[PATTERN-DEBUG] RootElement all properties: " + string.Join(" | ", nestedType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(p => p.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(60)));
                Logger.Info("[PATTERN-DEBUG] RootElement all fields: " + string.Join(" | ", nestedType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(60)));
            }

            foreach (var prop in nestedType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => IsInterestingMemberName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object nestedValue = prop.GetValue(value, null);
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": " + DescribeValue(nestedValue));
                    if (nestedValue != null && (string.Equals(prop.Name, "Attributes", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(prop.Name, "Children", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(prop.Name, "Objects", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(prop.Name, "Parent", StringComparison.OrdinalIgnoreCase)))
                    {
                        LogNestedAttributesDiagnostics(label + "." + prop.Name, nestedValue);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
                }
            }

            LogInterestingMethodResults(label, value, nestedType, resolvedObject);
        }

        private void LogInterestingMethodResults(string label, object value, Type nestedType, global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            TryLogMethodResult(label, value, nestedType, "GetDataUpdateProcess");
            TryLogMethodResult(label, value, nestedType, "GetPatternDefinition");

            var getPanelControls = nestedType.GetMethod("GetPanelControls", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getPanelControls != null)
            {
                TryLogMethodResult(label, value, getPanelControls, new object[] { resolvedObject });
            }

            var getVariablesSpec = nestedType.GetMethod("GetVariablesSpec", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getVariablesSpec != null)
            {
                TryLogMethodResult(label, value, getVariablesSpec, new object[] { resolvedObject });
            }
        }

        private void LogNestedAttributesDiagnostics(string label, object value)
        {
            var nestedType = value.GetType();
            Logger.Info("[PATTERN-DEBUG] " + label + " type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " properties: " + string.Join(" | ", GetInterestingPropertySignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " fields: " + string.Join(" | ", GetInterestingFieldSignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " methods: " + string.Join(" | ", GetInterestingMethodSignatures(nestedType)));

            foreach (var prop in nestedType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(25))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object nestedValue = prop.GetValue(value, null);
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": " + DescribeValue(nestedValue));
                }
                catch (Exception ex)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
                }
            }

            foreach (var method in nestedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.GetParameters().Length == 0)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                if (method.ReturnType == typeof(void)) continue;
                try
                {
                    object result = method.Invoke(value, Array.Empty<object>());
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + method.Name + "(): " + DescribeValue(result));
                }
                catch
                {
                }
            }

            if (string.Equals(nestedType.FullName, "Artech.Packages.Patterns.Objects.PatternInstanceElementChildren", StringComparison.Ordinal))
            {
                LogChildrenSample(label, value);
            }
        }

        private void LogChildrenSample(string label, object value)
        {
            try
            {
                var enumerable = value as System.Collections.IEnumerable;
                if (enumerable == null) return;
                var items = new List<object>();
                foreach (object item in enumerable)
                {
                    if (item != null) items.Add(item);
                }

                int index = 0;
                foreach (object item in items)
                {
                    LogPatternChildElement(label + "[" + index + "]", item, 0);
                    index++;
                    if (index >= 5) break;
                }

                foreach (object item in items)
                {
                    SearchPatternChildElementForTargets(label, item, 0);
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] " + label + " sample logging failed: <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogPatternChildElement(string label, object element, int depth)
        {
            if (depth > 6 || element == null) return;

            Type type = element.GetType();
            Logger.Info("[PATTERN-DEBUG] " + label + " type: " + (type.FullName ?? type.Name));

            foreach (string propName in new[] { "Name", "TypeName", "Caption", "PropertyTitle", "Path", "InternalPath", "KeyValueString" })
            {
                try
                {
                    var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop == null || prop.GetIndexParameters().Length > 0) continue;
                    object propValue = prop.GetValue(element, null);
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + propName + ": " + DescribeValue(propValue));
                }
                catch
                {
                }
            }

            if (ShouldLogElementAttributes(type, element))
            {
                TryLogTargetAttributes(label, type, element);
            }

            try
            {
                var childrenProp = type.GetProperty("Children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (childrenProp == null || childrenProp.GetIndexParameters().Length > 0) return;
                object children = childrenProp.GetValue(element, null);
                Logger.Info("[PATTERN-DEBUG] " + label + ".Children: " + DescribeValue(children));
                if (children is System.Collections.IEnumerable enumerable)
                {
                    int childIndex = 0;
                    foreach (object child in enumerable)
                    {
                        if (child == null) continue;
                        LogPatternChildElement(label + ".Children[" + childIndex + "]", child, depth + 1);
                        childIndex++;
                        if (childIndex >= 5) break;
                    }
                }

                string path = ReadStringProperty(type, element, "Path");
                string name = ReadStringProperty(type, element, "Name");
                if (string.Equals(name, "grid", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(path) && path.IndexOf("/grid[1]", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    LogAllGridChildren(label, children);
                }
            }
            catch
            {
            }
        }

        private bool ShouldLogElementAttributes(Type type, object element)
        {
            string name = ReadStringProperty(type, element, "Name");
            string path = ReadStringProperty(type, element, "Path");

            if (!string.IsNullOrEmpty(path) &&
                (path.IndexOf("/table", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 path.IndexOf("/grid", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(name, "table", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "grid", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "gridVariable", StringComparison.OrdinalIgnoreCase);
        }

        private void SearchPatternChildElementForTargets(string label, object element, int depth)
        {
            if (depth > 8 || element == null) return;

            Type type = element.GetType();
            string name = ReadStringProperty(type, element, "Name");
            string path = ReadStringProperty(type, element, "Path");
            string propertyTitle = ReadStringProperty(type, element, "PropertyTitle");
            string keyValue = ReadStringProperty(type, element, "KeyValueString");
            object attributes = GetReadablePropertyValue(element, "Attributes");
            string attributeName = TryGetPatternAttributeValue(attributes, "name");

            if (string.Equals(name, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attributeName, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attributeName, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyValue, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyValue, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyTitle, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyTitle, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(path) && (path.IndexOf("HorasDebito", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 path.IndexOf("SedCPHor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 path.IndexOf("TableGrid", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-NODE " + label + " depth=" + depth + " type=" + (type.FullName ?? type.Name));
                Logger.Info("[PATTERN-DEBUG] TARGET-NODE Name=" + name + "; AttributeName=" + attributeName + "; PropertyTitle=" + propertyTitle + "; KeyValueString=" + keyValue + "; Path=" + path);
                LogPatternElementHierarchy(label, element);
                TryLogTargetAttributes(label, type, element);
                LogPatternElementObjects(label, type, element);
            }

            try
            {
                var childrenProp = type.GetProperty("Children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (childrenProp == null || childrenProp.GetIndexParameters().Length > 0) return;
                object children = childrenProp.GetValue(element, null);
                if (children is System.Collections.IEnumerable enumerable)
                {
                    int childIndex = 0;
                    foreach (object child in enumerable)
                    {
                        if (child == null) continue;
                        SearchPatternChildElementForTargets(label + ".Children[" + childIndex + "]", child, depth + 1);
                        childIndex++;
                    }
                }
            }
            catch
            {
            }
        }

        private void TryLogTargetAttributes(string label, Type type, object element)
        {
            try
            {
                var attributesProp = type.GetProperty("Attributes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (attributesProp == null || attributesProp.GetIndexParameters().Length > 0) return;
                object attributes = attributesProp.GetValue(element, null);
                if (attributes == null) return;
                Logger.Info("[PATTERN-DEBUG] TARGET-ATTRIBUTES " + label + ": " + DescribeValue(attributes));
                LogNestedAttributesDiagnostics(label + ".Attributes", attributes);
                LogAttributeProperties(label + ".Attributes", attributes);
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-ATTRIBUTES " + label + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogPatternElementHierarchy(string label, object element)
        {
            try
            {
                var segments = new List<string>();
                object current = element;
                int depth = 0;
                while (current != null && depth < 12)
                {
                    Type currentType = current.GetType();
                    string currentName = FirstNonEmpty(
                        ReadStringProperty(currentType, current, "Name"),
                        ReadStringProperty(currentType, current, "PropertyTitle"),
                        ReadStringProperty(currentType, current, "KeyValueString"),
                        currentType.Name);
                    string currentPath = ReadStringProperty(currentType, current, "Path");
                    segments.Add(currentName + " {" + currentPath + "}");

                    var parentProp = currentType.GetProperty("Parent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (parentProp == null || parentProp.GetIndexParameters().Length > 0)
                    {
                        break;
                    }

                    current = parentProp.GetValue(current, null);
                    depth++;
                }

                if (segments.Count > 0)
                {
                    Logger.Info("[PATTERN-DEBUG] TARGET-HIERARCHY " + label + ": " + string.Join(" <= ", segments));
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-HIERARCHY " + label + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogPatternElementObjects(string label, Type type, object element)
        {
            try
            {
                var objectsProp = type.GetProperty("Objects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (objectsProp == null || objectsProp.GetIndexParameters().Length > 0) return;

                object objects = objectsProp.GetValue(element, null);
                Logger.Info("[PATTERN-DEBUG] TARGET-OBJECTS " + label + ": " + DescribeValue(objects));
                if (!(objects is System.Collections.IEnumerable enumerable)) return;

                int index = 0;
                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    Logger.Info("[PATTERN-DEBUG] TARGET-OBJECTS " + label + "[" + index + "]=" + DescribeValue(item));
                    index++;
                    if (index >= 10) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-OBJECTS " + label + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogAllGridChildren(string label, object children)
        {
            if (!(children is System.Collections.IEnumerable enumerable)) return;

            int index = 0;
            foreach (object child in enumerable)
            {
                if (child == null) continue;
                Type childType = child.GetType();
                string childPath = ReadStringProperty(childType, child, "Path");
                string childName = ReadStringProperty(childType, child, "Name");
                string childTitle = ReadStringProperty(childType, child, "PropertyTitle");
                Logger.Info("[PATTERN-DEBUG] GRID-CHILD " + label + "[" + index + "] Name=" + childName + "; PropertyTitle=" + childTitle + "; Path=" + childPath);
                TryLogTargetAttributes(label + "[" + index + "]", childType, child);
                index++;
                if (index >= 40) break;
            }
        }

        private void LogAttributeProperties(string label, object attributes)
        {
            try
            {
                var attrType = attributes.GetType();
                var propertiesProp = attrType.GetProperty("Properties", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (propertiesProp == null || propertiesProp.GetIndexParameters().Length > 0) return;
                object properties = propertiesProp.GetValue(attributes, null);
                if (!(properties is System.Collections.IEnumerable enumerable)) return;

                int count = 0;
                foreach (object prop in enumerable)
                {
                    if (prop == null) continue;
                    Type propType = prop.GetType();
                    string name = ReadStringProperty(propType, prop, "Name");
                    string value = ReadStringProperty(propType, prop, "Value");
                    Logger.Info("[PATTERN-DEBUG] ATTR-PROP " + label + " " + name + "=" + value);
                    count++;
                    if (count >= 40) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] ATTR-PROP " + label + " <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private string ReadStringProperty(Type type, object instance, string propertyName)
        {
            try
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null || prop.GetIndexParameters().Length > 0) return null;
                object value = prop.GetValue(instance, null);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void TryLogMethodResult(string label, object value, Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return;
            TryLogMethodResult(label, value, method, Array.Empty<object>());
        }

        private void TryLogMethodResult(string label, object value, MethodInfo method, object[] args)
        {
            try
            {
                object result = method.Invoke(value, args);
                Logger.Info("[PATTERN-DEBUG] " + label + "." + method.Name + "(): " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] " + label + "." + method.Name + "(): <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogNamedInterfaceProperties(string stage, object target, string interfaceFullName)
        {
            if (target == null || string.IsNullOrWhiteSpace(interfaceFullName)) return;

            try
            {
                Type iface = target.GetType().GetInterfaces()
                    .FirstOrDefault(i => string.Equals(i.FullName, interfaceFullName, StringComparison.OrdinalIgnoreCase));
                if (iface == null) return;

                foreach (var prop in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        object value = prop.GetValue(target, null);
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + prop.Name + "=" + DescribeValue(value));
                    }
                    catch (Exception exProp)
                    {
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + prop.Name + "=<error " + exProp.GetType().Name + ": " + exProp.Message + ">");
                    }
                }

                foreach (var method in iface.GetMethods()
                    .Where(m => m.GetParameters().Length == 0 && m.ReturnType != typeof(void))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        object result = method.Invoke(target, null);
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + method.Name + "()=" + DescribeValue(result));
                    }
                    catch (Exception exMethod)
                    {
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + method.Name + "()=<error " + exMethod.GetType().Name + ": " + exMethod.Message + ">");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Interface logging failed for " + interfaceFullName + " at " + stage + ": " + ex.Message);
            }
        }

        private void TryInvokeInterfaceMethodByName(object target, string interfaceFullName, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(interfaceFullName) || string.IsNullOrWhiteSpace(methodName)) return;

            try
            {
                Type iface = target.GetType().GetInterfaces()
                    .FirstOrDefault(i => string.Equals(i.FullName, interfaceFullName, StringComparison.OrdinalIgnoreCase));
                if (iface == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Interface method not found because interface is missing: " + interfaceFullName + "." + methodName);
                    return;
                }

                MethodInfo method = iface.GetMethods()
                    .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == args.Length);
                if (method == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Interface method not found: " + interfaceFullName + "." + methodName);
                    return;
                }

                object result = method.Invoke(target, args);
                Logger.Info("[PATTERN-DEBUG] Interface invoke " + iface.Name + "." + method.Name + "() => " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Interface method failed " + interfaceFullName + "." + methodName + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogPatternDefinitionObject(object patternInstance)
        {
            if (patternInstance == null) return;

            try
            {
                MethodInfo getPatternDefinition = patternInstance.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "GetPatternDefinition", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (getPatternDefinition == null) return;

                object definition = getPatternDefinition.Invoke(patternInstance, null);
                if (definition == null)
                {
                    Logger.Info("[PATTERN-DEBUG] GetPatternDefinition()=<null>");
                    return;
                }

                Type defType = definition.GetType();
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(defType)));
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() ctors: " + string.Join(" | ", defType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(ctor => FormatConstructorSignature(ctor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() properties: " + string.Join(" | ", defType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(40)));
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() methods: " + string.Join(" | ", defType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("validator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("implement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(60)));

                TryLogPatternImplementationObject(definition);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] GetPatternDefinition() logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogPatternImplementationObject(object definition)
        {
            if (definition == null) return;

            try
            {
                PropertyInfo implProp = definition.GetType().GetProperty("PatternImplementation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object implementation = implProp?.GetValue(definition, null);
                if (implementation == null)
                {
                    Logger.Info("[PATTERN-DEBUG] PatternImplementation=<null>");
                    return;
                }

                Type implType = implementation.GetType();
                Logger.Info("[PATTERN-DEBUG] PatternImplementation type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(implType)));
                Logger.Info("[PATTERN-DEBUG] PatternImplementation ctors: " + string.Join(" | ", implType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(ctor => FormatConstructorSignature(ctor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
                Logger.Info("[PATTERN-DEBUG] PatternImplementation properties: " + string.Join(" | ", implType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(40)));
                Logger.Info("[PATTERN-DEBUG] PatternImplementation methods: " + string.Join(" | ", implType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("validator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("setting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(80)));

                foreach (string methodName in new[]
                {
                    "GetInstanceValidator",
                    "GetInstanceUpdateProcess",
                    "GetInstanceVersionAdapter",
                    "GetInstanceOneSource",
                    "GetInstanceSources",
                    "GetInstanceEditorHelper",
                    "GetSettingsValidator",
                    "GetSettingsUpdateProcess",
                    "GetSettingsVersionAdapter",
                    "GetSettingsEditorHelper"
                })
                {
                    try
                    {
                        MethodInfo method = implType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                        if (method == null)
                        {
                            Logger.Info("[PATTERN-DEBUG] PatternImplementation." + methodName + "()=<missing>");
                            continue;
                        }

                        object result = method.Invoke(implementation, null);
                        Logger.Info("[PATTERN-DEBUG] PatternImplementation." + methodName + "()=" + DescribeValue(result));
                        if (result != null &&
                            (methodName.IndexOf("EditorHelper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             methodName.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            LogPatternImplementationResultObject("PatternImplementation." + methodName + "()", result);
                        }
                    }
                    catch (Exception exMethod)
                    {
                        Logger.Warn("[PATTERN-DEBUG] PatternImplementation." + methodName + "() failed: " + (exMethod.InnerException?.Message ?? exMethod.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] PatternImplementation logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogPatternImplementationResultObject(string label, object value)
        {
            if (value == null) return;

            try
            {
                Type type = value.GetType();
                Logger.Info("[PATTERN-DEBUG] " + label + " type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(type)));
                Logger.Info("[PATTERN-DEBUG] " + label + " properties: " + string.Join(" | ", type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(p => p.Name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(60)));
                Logger.Info("[PATTERN-DEBUG] " + label + " methods: " + string.Join(" | ", type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(80)));

                MethodInfo createEditors = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "CreateEditors", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (createEditors != null)
                {
                    try
                    {
                        object editors = createEditors.Invoke(value, null);
                        Logger.Info("[PATTERN-DEBUG] " + label + ".CreateEditors()=" + DescribeValue(editors));
                        if (editors is System.Collections.IEnumerable enumerable)
                        {
                            int index = 0;
                            foreach (object item in enumerable)
                            {
                                if (item == null) continue;
                                Type itemType = item.GetType();
                                Logger.Info("[PATTERN-DEBUG] " + label + ".CreateEditors()[" + index + "] type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(itemType)));
                                Logger.Info("[PATTERN-DEBUG] " + label + ".CreateEditors()[" + index + "] properties: " + string.Join(" | ", itemType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    .Where(p => p.Name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
                                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                    .Take(60)));
                                index++;
                                if (index >= 15) break;
                            }
                        }
                    }
                    catch (Exception exCreate)
                    {
                        Exception root = exCreate is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : exCreate;
                        Logger.Warn("[PATTERN-DEBUG] " + label + ".CreateEditors() failed: " + root.GetType().FullName + ": " + root.Message);
                        Logger.Warn("[PATTERN-DEBUG] " + label + ".CreateEditors() stack: " + (root.StackTrace ?? "<no-stack>"));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + " diagnostics failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogSemanticWorkWithPlusInstance(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            if (resolvedObject == null) return;

            WritePatternDebugTrace("TryLogSemanticWorkWithPlusInstance object=" + resolvedObject.Name + " type=" + (resolvedObject.TypeDescriptor?.Name ?? "<null>"));

            try
            {
                Type semanticInstanceType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly => assembly.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPlusInstance", false, true))
                    .FirstOrDefault(type => type != null);
                if (semanticInstanceType == null)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic instance type not loaded.");
                    return;
                }

                ConstructorInfo ctor = semanticInstanceType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var parameters = c.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(resolvedObject);
                    });
                if (ctor == null)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic instance ctor not compatible with resolved object type.");
                    return;
                }

                object semanticInstance = ctor.Invoke(new object[] { resolvedObject });
                Logger.Info("[PATTERN-DEBUG] Semantic instance created: " + DescribeValue(semanticInstance));
                WritePatternDebugTrace("Semantic instance created=" + DescribeValue(semanticInstance));
                TryInvokeSemanticInitialize("Semantic instance", semanticInstance);

                LogSemanticGridSettings(semanticInstance);
                LogSemanticGridTargets(semanticInstance, "Instance");
                LogWorkWithPlusSemanticTypeCandidates();

                object settings = GetReadablePropertyValue(semanticInstance, "Settings");
                if (settings != null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings object: " + DescribeValue(settings));
                    WritePatternDebugTrace("Semantic settings object=" + DescribeValue(settings));
                    TryInvokeSemanticInitialize("Semantic settings", settings);
                    LogSemanticGridTargets(settings, "Settings");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Semantic WorkWithPlus logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogSemanticGridSettings(object semanticInstance)
        {
            if (semanticInstance == null) return;

            try
            {
                object settings = GetReadablePropertyValue(semanticInstance, "Settings");
                if (settings == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings unavailable.");
                    return;
                }

                MethodInfo getAllChildren = settings.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings GetAllChildren() unavailable.");
                    return;
                }

                var allChildren = getAllChildren.Invoke(settings, null) as System.Collections.IEnumerable;
                Logger.Info("[PATTERN-DEBUG] Semantic settings GetAllChildren count=" + CountEnumerable(allChildren));
                WritePatternDebugTrace("Semantic settings GetAllChildren count=" + CountEnumerable(allChildren));
                LogSemanticChildTypeSummary("Semantic settings", allChildren);
                if (allChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings returned no children.");
                    WritePatternDebugTrace("Semantic settings returned no children.");
                    return;
                }

                foreach (object child in EnumerateSemanticItems(allChildren))
                {
                    if (child == null) continue;
                    if (!string.Equals(child.GetType().FullName, "DVelop.Patterns.WorkWithPlus.SettingsGridElement", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Logger.Info("[PATTERN-DEBUG] Semantic settings grid: " + DescribeSemanticElement(child));
                    Logger.Info("[PATTERN-DEBUG] Semantic settings grid AlwaysUseColumnTitleProperty=" + DescribeValue(GetReadablePropertyValue(child, "AlwaysUseColumnTitleProperty")));
                    WritePatternDebugTrace("Semantic settings grid=" + DescribeSemanticElement(child));
                    WritePatternDebugTrace("Semantic settings grid AlwaysUseColumnTitleProperty=" + DescribeValue(GetReadablePropertyValue(child, "AlwaysUseColumnTitleProperty")));
                    LogSemanticTypeSurface("Semantic settings grid surface", child);
                    TryProbeSemanticGridLookup("Semantic settings grid lookup", child);
                    LogSemanticNestedChildren("Semantic settings grid", child);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Semantic settings logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogSemanticGridTargets(object semanticRoot, string label)
        {
            if (semanticRoot == null) return;

            try
            {
                MethodInfo getAllChildren = semanticRoot.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " GetAllChildren() unavailable.");
                    return;
                }

                var allChildren = getAllChildren.Invoke(semanticRoot, null) as System.Collections.IEnumerable;
                Logger.Info("[PATTERN-DEBUG] Semantic " + label + " GetAllChildren count=" + CountEnumerable(allChildren));
                WritePatternDebugTrace("Semantic " + label + " GetAllChildren count=" + CountEnumerable(allChildren));
                LogSemanticChildTypeSummary("Semantic " + label, allChildren);
                if (allChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " returned no children.");
                    WritePatternDebugTrace("Semantic " + label + " returned no children.");
                    return;
                }

                foreach (object child in EnumerateSemanticItems(allChildren))
                {
                    if (child == null) continue;
                    Type childType = child.GetType();
                    if (string.Equals(childType.FullName, "DVelop.Patterns.WorkWithPlus.WPGridElement", StringComparison.Ordinal) ||
                        string.Equals(childType.FullName, "DVelop.Patterns.WorkWithPlus.SettingsGridWPElement", StringComparison.Ordinal))
                    {
                        Logger.Info("[PATTERN-DEBUG] Semantic " + label + " grid: " + DescribeSemanticElement(child));
                        WritePatternDebugTrace("Semantic " + label + " grid=" + DescribeSemanticElement(child));
                        LogSemanticTypeSurface("Semantic " + label + " grid surface", child);
                        TryProbeSemanticGridLookup("Semantic " + label + " grid lookup", child);
                    }

                    string name = GetReadablePropertyValue(child, "Name")?.ToString();
                    string attributeName = GetReadablePropertyValue(child, "AttributeName")?.ToString();
                    bool isTarget = string.Equals(name, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(name, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(attributeName, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(attributeName, "SedCPHor", StringComparison.OrdinalIgnoreCase);
                    if (!isTarget)
                    {
                        continue;
                    }

                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " target: " + DescribeSemanticElement(child));
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " target Description=" + DescribeValue(GetReadablePropertyValue(child, "Description")));
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " target Visible=" + DescribeValue(GetReadablePropertyValue(child, "Visible")));
                    WritePatternDebugTrace("Semantic " + label + " target=" + DescribeSemanticElement(child));
                    WritePatternDebugTrace("Semantic " + label + " target Description=" + DescribeValue(GetReadablePropertyValue(child, "Description")));
                    WritePatternDebugTrace("Semantic " + label + " target Visible=" + DescribeValue(GetReadablePropertyValue(child, "Visible")));
                    LogSemanticTypeSurface("Semantic " + label + " target surface", child);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Semantic " + label + " logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogSemanticNestedChildren(string label, object semanticElement)
        {
            if (semanticElement == null) return;

            try
            {
                MethodInfo getAllChildren = semanticElement.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + " GetAllChildren() unavailable.");
                    return;
                }

                var nestedChildren = getAllChildren.Invoke(semanticElement, null) as System.Collections.IEnumerable;
                Logger.Info("[PATTERN-DEBUG] " + label + " GetAllChildren count=" + CountEnumerable(nestedChildren));
                WritePatternDebugTrace(label + " GetAllChildren count=" + CountEnumerable(nestedChildren));
                LogSemanticChildTypeSummary(label, nestedChildren);
                LogSemanticGridTargets(semanticElement, label);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + " nested logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogWorkWithPlusSemanticTypeCandidates()
        {
            try
            {
                Assembly workWithPlusAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(assembly =>
                        assembly.GetName().Name != null &&
                        assembly.GetName().Name.IndexOf("WorkWithPlus", StringComparison.OrdinalIgnoreCase) >= 0);

                if (workWithPlusAssembly == null)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus assembly not loaded for candidate scan.");
                    return;
                }

                var interesting = workWithPlusAssembly.GetTypes()
                    .Where(type =>
                        type.FullName != null &&
                        (type.FullName.IndexOf("Change", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Merge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Comparer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Command", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Grid", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(type => type.FullName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Take(120)
                    .ToList();

                Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic candidate types count=" + interesting.Count);
                foreach (string typeName in interesting)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic candidate type=" + typeName);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] WorkWithPlus candidate scan failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryInvokeSemanticInitialize(string label, object instance)
        {
            if (instance == null) return;

            try
            {
                MethodInfo initialize = instance.GetType().GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (initialize == null)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + " Initialize() unavailable.");
                    return;
                }

                object result = initialize.Invoke(instance, null);
                Logger.Info("[PATTERN-DEBUG] " + label + ".Initialize()=" + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + ".Initialize() failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private int CountEnumerable(System.Collections.IEnumerable enumerable)
        {
            if (enumerable == null) return -1;

            int count = 0;
            foreach (object _ in EnumerateSemanticItems(enumerable))
            {
                count++;
                if (count >= 5000) break;
            }

            return count;
        }

        private void LogSemanticChildTypeSummary(string label, System.Collections.IEnumerable enumerable)
        {
            if (enumerable == null) return;

            try
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                int seen = 0;
                foreach (object item in EnumerateSemanticItems(enumerable))
                {
                    if (item == null) continue;
                    string typeName = item.GetType().FullName ?? item.GetType().Name;
                    counts[typeName] = counts.TryGetValue(typeName, out int current) ? current + 1 : 1;
                    seen++;
                    if (seen >= 5000) break;
                }

                foreach (var entry in counts.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + " child-type " + entry.Key + " count=" + entry.Value);
                    WritePatternDebugTrace(label + " child-type " + entry.Key + " count=" + entry.Value);
                }

                int sampleIndex = 0;
                foreach (object item in EnumerateSemanticItems(enumerable))
                {
                    if (item == null) continue;
                    Logger.Info("[PATTERN-DEBUG] " + label + " child-sample[" + sampleIndex + "] " + DescribeSemanticElement(item));
                    WritePatternDebugTrace(label + " child-sample[" + sampleIndex + "] " + DescribeSemanticElement(item));
                    sampleIndex++;
                    if (sampleIndex >= 20) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + " child-type summary failed: " + ex.Message);
            }
        }

        private IEnumerable<object> EnumerateSemanticItems(System.Collections.IEnumerable enumerable)
        {
            if (enumerable == null) yield break;

            foreach (object item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                if (item is string)
                {
                    yield return item;
                    continue;
                }

                if (item is System.Collections.IEnumerable nested)
                {
                    foreach (object nestedItem in EnumerateSemanticItems(nested))
                    {
                        if (nestedItem == null) continue;
                        yield return nestedItem;
                    }

                    continue;
                }

                yield return item;
            }
        }

        private string DescribeSemanticElement(object instance)
        {
            if (instance == null) return "<null>";

            try
            {
                string typeName = instance.GetType().FullName ?? instance.GetType().Name;
                string name = GetReadablePropertyValue(instance, "Name")?.ToString();
                string attributeName = GetReadablePropertyValue(instance, "AttributeName")?.ToString();
                string description = GetReadablePropertyValue(instance, "Description")?.ToString();
                string visible = DescribeValue(GetReadablePropertyValue(instance, "Visible"));
                string element = DescribeValue(GetReadablePropertyValue(instance, "Element"));
                return typeName + " Name=" + (name ?? "<null>") + " AttributeName=" + (attributeName ?? "<null>") + " Description=" + (description ?? "<null>") + " Visible=" + visible + " Element=" + element;
            }
            catch (Exception ex)
            {
                return "<error " + ex.GetType().Name + ": " + ex.Message + ">";
            }
        }

        private void LogSemanticTypeSurface(string label, object target)
        {
            if (target == null) return;

            try
            {
                Type type = target.GetType();
                string typeName = type.FullName ?? type.Name;
                WritePatternDebugTrace(label + " type=" + typeName);
                WritePatternDebugTrace(label + " properties=" + string.Join(" | ",
                    type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Select(prop => prop.Name + ":" + (prop.PropertyType.Name ?? "<null>"))
                        .Take(80)));
                WritePatternDebugTrace(label + " methods=" + string.Join(" | ",
                    type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(method => !method.IsSpecialName)
                        .Select(method => FormatMethodSignature(method))
                        .Take(80)));
            }
            catch (Exception ex)
            {
                WritePatternDebugTrace(label + " surface error=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TryProbeSemanticGridLookup(string label, object target)
        {
            if (target == null) return;

            try
            {
                Type type = target.GetType();
                MethodInfo findMethod = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "FindWPGridAttribute", StringComparison.OrdinalIgnoreCase) &&
                        method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType == typeof(string));

                if (findMethod == null)
                {
                    WritePatternDebugTrace(label + " FindWPGridAttribute=<missing>");
                    return;
                }

                foreach (string targetName in new[] { "HorasDebito", "SedCPHor" })
                {
                    object result = findMethod.Invoke(target, new object[] { targetName });
                    WritePatternDebugTrace(label + " FindWPGridAttribute(" + targetName + ")=" + DescribeValue(result));
                    if (result != null)
                    {
                        LogSemanticTypeSurface(label + " result " + targetName, result);
                    }
                }
            }
            catch (Exception ex)
            {
                WritePatternDebugTrace(label + " lookup error=" + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void WritePatternDebugTrace(string message)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(message)) return;

            try
            {
                string directory = ResolvePatternDebugDirectory();
                Directory.CreateDirectory(directory);
                string filePath = Path.Combine(directory, "wwp_semantic_debug.txt");
                File.AppendAllText(filePath, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private string ResolvePatternDebugDirectory()
        {
            string configuredDirectory = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG_DIR");
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                return configuredDirectory;
            }

            return RuntimePaths.TempRoot;
        }
    }
}
