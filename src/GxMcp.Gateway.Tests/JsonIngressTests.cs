using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class JsonIngressTests
    {
        [Fact]
        public void ParseObject_PreservesIsoTimestampAsExactString()
        {
            const string since = "2026-09-21T11:05:00.000-03:00";
            const string before = "2026-09-22T14:30:11.120+05:30";

            JObject query = JsonIngress.ParseObject(
                "{\"query\":\"Customer\",\"since\":\"" + since + "\",\"modifiedBefore\":\"" + before + "\"}");
            JObject list = JsonIngress.ParseObject(
                "{\"since\":\"" + since + "\",\"modifiedBefore\":\"" + before + "\"}");
            JObject logs = JsonIngress.ParseObject(
                "{\"action\":\"logs\",\"since\":\"" + since + "\"}");

            var samples = new[]
            {
                ("genexus_query", query),
                ("genexus_list_objects", list),
                ("genexus_telemetry", logs)
            };
            GatewayArgsValidator.ClearCache();
            foreach (var (toolName, arguments) in samples)
            {
                Assert.Equal(JTokenType.String, arguments["since"]?.Type);
                Assert.Contains("\"since\":\"" + since + "\"", arguments.ToString(Formatting.None));
                if (arguments["modifiedBefore"] != null)
                {
                    Assert.Equal(JTokenType.String, arguments["modifiedBefore"]?.Type);
                    Assert.Contains("\"modifiedBefore\":\"" + before + "\"", arguments.ToString(Formatting.None));
                }

                var result = GatewayArgsValidator.Validate(toolName, arguments);
                Assert.True(result.Ok, string.Join("; ", result.Violations.Select(v => v.Path + ":" + v.Actual)));
            }
        }

        [Fact]
        public void IngressFilesDoNotUseDefaultNewtonsoftDateParsing()
        {
            string repoRoot = FindRepositoryRoot();
            string[] ingressFiles =
            {
                "src/GxMcp.Gateway/Program.cs",
                "src/GxMcp.Gateway/Program.Http.cs",
                "src/GxMcp.Gateway/SharedWorkerConnection.cs",
                "src/GxMcp.Worker/Program.cs",
                "src/GxMcp.Worker/SharedWorkerHost.cs",
                "src/GxMcp.Worker/SharedWorkerHostProtocol.cs"
            };
            var defaultParser = new Regex(
                @"\b(?:JObject|JToken)\s*\.\s*Parse\s*\(|\bJsonConvert\s*\.\s*DeserializeObject\s*<\s*JObject\s*>",
                RegexOptions.CultureInvariant);

            foreach (string relativePath in ingressFiles)
            {
                string source = File.ReadAllText(Path.Combine(repoRoot, relativePath));
                source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
                source = Regex.Replace(source, @"^\s*//.*?$", "", RegexOptions.Multiline);
                Assert.DoesNotMatch(defaultParser, source);
            }

            string dispatcher = File.ReadAllText(Path.Combine(repoRoot, "src/GxMcp.Worker/Services/CommandDispatcher.cs"));
            dispatcher = Regex.Replace(dispatcher, @"/\*.*?\*/", "", RegexOptions.Singleline);
            dispatcher = Regex.Replace(dispatcher, @"^\s*//.*?$", "", RegexOptions.Multiline);
            Assert.DoesNotMatch(
                new Regex(@"\b(?:JObject|JToken)\s*\.\s*Parse\s*\(\s*(?:line|rawLine)\b", RegexOptions.CultureInvariant),
                dispatcher);
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the Genexus18MCP repository root.");
        }
    }
}
