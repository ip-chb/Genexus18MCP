using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class CallSiteMatcherTests
    {
        [Theory]
        [InlineData("ProcB.Call(&a, &b)", "ProcB", "")]
        [InlineData("&x = ProcB.Udp(&a)", "ProcB", "")]
        [InlineData("ProcB.Submit(&a)", "ProcB", "")]
        [InlineData("&url = ProcB.Link(&a)", "ProcB", "")]
        [InlineData("Module1.ProcB.Call(&a)", "ProcB", "Module1.ProcB")]
        [InlineData("call(ProcB, &a)", "ProcB", "")]
        [InlineData("call('ProcB', &a)", "ProcB", "")]
        [InlineData("&x = ProcB(&a)", "ProcB", "")]
        public void MatchesSupportedGeneXusCallSyntax(string source, string canonicalName, string moduleQualifiedName)
        {
            var call = Assert.Single(SourceParser.ParseCalls(source));

            Assert.True(CallSiteMatcher.Matches(call, canonicalName, moduleQualifiedName));
        }

        [Theory]
        [InlineData("&ProcB.Call()")]
        [InlineData("SomethingProcB.Call()")]
        [InlineData("other.Call(ProcB)")]
        public void DoesNotMatchVariablesOrNearNames(string source)
        {
            var call = Assert.Single(SourceParser.ParseCalls(source));

            Assert.False(CallSiteMatcher.Matches(call, "ProcB", "Module1.ProcB"));
        }

        [Fact]
        public void DoesNotMatchCallsFoundOnlyInComments()
        {
            Assert.Empty(SourceParser.ParseCalls("// ProcB.Call()"));
        }
    }
}
