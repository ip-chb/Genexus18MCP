using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Friction 2026-05-22 #63: every error envelope should carry a structured
    // "what to do next" hint. Pre-existing suggested_next_step is preserved;
    // otherwise McpRouter.AttachSuggestedNextStep synthesizes one from the
    // code / message pattern. Covers the four cases the spec called out.
    public class SuggestedNextStepTests
    {
        [Fact]
        public void PatchNoMatch_PointsAtNearMatchInspection()
        {
            var err = JObject.Parse(@"{""code"":""patch_no_match"",""message"":""Context not found.""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("inspect_near_match", hint["action"]!.ToString());
            Assert.Contains("nearMatches", hint["hint"]!.ToString());
        }

        [Fact]
        public void PatchAmbiguous_AlsoRoutesToNearMatchHint()
        {
            // 'Ambiguous patch: Found N exact matches' is the sibling of NoMatch
            // (matching produced too many hits, not zero). The same near-match
            // / replaceAll guidance applies.
            var err = JObject.Parse(@"{""code"":""Error"",""message"":""Ambiguous patch: Found 3 exact matches, but expected 1.""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("inspect_near_match", hint["action"]!.ToString());
        }

        [Fact]
        public void VisualWriteFailure_PointsAtLayoutGotchaScanner()
        {
            var err = JObject.Parse(@"{""status"":""Error"",""error"":""Invalid visual XML: unclosed gxButton tag""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("run_layout_gotcha_scanner", hint["action"]!.ToString());
            Assert.Contains("layoutGotchas", hint["hint"]!.ToString());
        }

        [Fact]
        public void KbAmbiguous_PointsAtKbParameter()
        {
            var err = JObject.Parse(@"{""code"":""KB_AMBIGUOUS"",""message"":""KB_AMBIGUOUS: multiple KBs open""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("specify_kb", hint["action"]!.ToString());
            Assert.Contains("kb=", hint["hint"]!.ToString());
            Assert.Contains("set_default", hint["hint"]!.ToString());
        }

        [Fact]
        public void Spc0150_PointsAtExtractToProcedureRecipe()
        {
            var err = JObject.Parse(@"{""code"":""Error"",""error"":""spc0150 — Attribute cannot be assigned in this context""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("recipe_extract_to_procedure", hint["action"]!.ToString());
            Assert.Equal("extract_to_procedure", hint["recipe"]!.ToString());
        }

        [Fact]
        public void PartNotFound_PointsAtReadFullObject()
        {
            var err = JObject.Parse(@"{""code"":""PartNotFound"",""message"":""Part 'Foo' does not exist""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("read_full_object", hint["action"]!.ToString());
            Assert.Equal("genexus_read", hint["tool"]!.ToString());
        }

        [Fact]
        public void ObjectNotFound_PointsAtQuery()
        {
            var err = JObject.Parse(@"{""code"":""ObjectNotFound"",""message"":""Object not found: UnknownObj""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.NotNull(hint);
            Assert.Equal("search_objects", hint["action"]!.ToString());
            Assert.Equal("genexus_query", hint["tool"]!.ToString());
        }

        [Fact]
        public void UnknownError_ReturnsNull()
        {
            // No registered pattern → null. Callers fall back to message/hint.
            var err = JObject.Parse(@"{""code"":""SomeNewErrorNobodyRegistered"",""message"":""..""}");
            var hint = McpRouter.AttachSuggestedNextStep(err);
            Assert.Null(hint);
        }

        [Fact]
        public void TrimErrorEnvelope_PreservesPreExistingSuggestedNextStep()
        {
            // Worker-side payloads (e.g. write_not_persisted) carry their own
            // suggested_next_step. Trim must not clobber it.
            var err = JObject.Parse(@"{""code"":""write_not_persisted"",""message"":""..."",""suggested_next_step"":""Retry the same patch.""}");
            var trimmed = McpRouter.TrimErrorEnvelope(err, verbose: false);
            Assert.NotNull(trimmed["suggested_next_step"]);
            Assert.Equal("Retry the same patch.", trimmed["suggested_next_step"]!.ToString());
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsPatternChoiceLists()
        {
            // Issue #260: nested (errorExtra) and top-level (extra) pattern choice lists
            // survive the terse projection; the caller needs them to pick the instance.
            var err = JObject.Parse(@"{""status"":""error"",
                ""error"":{""code"":""PatternInstanceAmbiguous"",""message"":""'DV290' has 2 pattern instances"",
                    ""candidates"":[{""name"":""K2BEntityServicesDV290"",""pattern"":""K2BEntityServices""}]},
                ""detectedPatterns"":[{""name"":""K2BTrnFormDV290"",""pattern"":""K2BTrnForm""}]}");

            var trimmed = McpRouter.TrimErrorEnvelope(err, verbose: false);

            Assert.Equal("K2BEntityServicesDV290", trimmed["candidates"]?[0]?["name"]?.ToString());
            Assert.Equal("K2BTrnForm", trimmed["detectedPatterns"]?[0]?["pattern"]?.ToString());
            Assert.Null(trimmed["availablePatterns"]);
        }

        [Fact]
        public void TrimErrorEnvelope_NoMatchFromShorthandDryRunPreservesNearMatchDiagnostics()
        {
            var workerResult = JObject.Parse(@"{""status"":""error"",
                ""error"":{""code"":""NoMatch"",""message"":""Context block not found."",
                    ""part"":""Events"",""noNearMatchHint"":""Re-read a smaller exact block."",
                    ""nearMatches"":[{""line"":12,""snippet"":""Event Start""}],
                    ""eolDiff"":[{""lineNo"":12,""agent"":""Event Start"",""file"":""Event Start""}],
                    ""did_you_mean"":[""EventStart""]}}");

            var response = McpRouter.TrimErrorEnvelope(workerResult, verbose: false);

            Assert.Equal("NoMatch", response["code"]?.ToString());
            Assert.NotNull(response["nearMatches"]);
            Assert.NotNull(response["eolDiff"]);
            Assert.NotNull(response["did_you_mean"]);
            Assert.Equal("Re-read a smaller exact block.", response["noNearMatchHint"]?.ToString());
            Assert.Equal("inspect_near_match", response["suggested_next_step"]?["action"]?.ToString());
            Assert.Contains("response.nearMatches", response["suggested_next_step"]?["hint"]?.ToString());
        }

        [Fact]
        public void TrimErrorEnvelope_SynthesizesHint_WhenNoneOnPayload()
        {
            var err = JObject.Parse(@"{""code"":""patch_no_match"",""message"":""Context not found.""}");
            var trimmed = McpRouter.TrimErrorEnvelope(err, verbose: false);
            Assert.NotNull(trimmed["suggested_next_step"]);
        }

        [Fact]
        public void TrimErrorEnvelope_PreservesDiagnosticContext()
        {
            var err = JObject.Parse(@"{""code"":""Error"",""message"":""Fatal crash"",""diagnosticContext"":{""sdk"":{""major"":""16""},""reportIssue"":""Please report""}}");
            var trimmed = McpRouter.TrimErrorEnvelope(err, verbose: false);
            Assert.NotNull(trimmed["diagnosticContext"]);
            Assert.Equal("16", trimmed["diagnosticContext"]!["sdk"]!["major"]!.ToString());
            Assert.Equal("Please report", trimmed["diagnosticContext"]!["reportIssue"]!.ToString());
        }

        [Fact]
        public void BuildDiagnosticContext_CarriesSupportedMajorsAndReportGuidance()
        {
            var diag = Program.BuildDiagnosticContext();
            Assert.NotNull(diag);
            Assert.NotNull(diag["sdk"]);
            Assert.NotNull(diag["sdk"]!["supportedMajors"]);
            var supported = (JArray)diag["sdk"]!["supportedMajors"]!;
            Assert.Contains("16", supported.Select(t => t.ToString()));
            Assert.Contains("17", supported.Select(t => t.ToString()));
            Assert.Contains("18", supported.Select(t => t.ToString()));
            Assert.NotNull(diag["reportIssue"]);
            Assert.Contains("collect-diagnostics.ps1", diag["reportIssue"]!.ToString());
            Assert.Contains("github.com/lennix1337/Genexus18MCP/issues", diag["reportIssue"]!.ToString());
        }
    }
}
