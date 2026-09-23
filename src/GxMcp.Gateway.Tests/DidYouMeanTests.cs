using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    public class DidYouMeanTests
    {
        [Theory]
        [InlineData("", "", 0)]
        [InlineData("abc", "abc", 0)]
        [InlineData("abc", "abd", 1)]
        [InlineData("kitten", "sitting", 3)]
        [InlineData("set_atribute", "set_attribute", 1)]
        [InlineData("Set_Attribute", "set_attribute", 0)]
        public void Levenshtein_ComputesExpectedDistance(string a, string b, int expected)
        {
            Assert.Equal(expected, DidYouMean.Levenshtein(a, b));
        }

        [Fact]
        public void Suggest_PicksClosestWithinThreshold()
        {
            var candidates = new[] { "set_attribute", "add_attribute", "remove_attribute" };
            Assert.Equal("set_attribute", DidYouMean.Suggest("set_atribute", candidates));
            Assert.Equal("remove_attribute", DidYouMean.Suggest("remove_atribute", candidates));
        }

        [Fact]
        public void Suggest_ReturnsNullWhenNothingClose()
        {
            var candidates = new[] { "set_attribute", "add_rule" };
            Assert.Null(DidYouMean.Suggest("delete_everything", candidates));
        }

        [Fact]
        public void FormatSuggestionMessage_IncludesSuggestionAndAllowed()
        {
            var candidates = new[] { "xml", "ops", "patch", "full" };
            string msg = DidYouMean.FormatSuggestionMessage("edit mode", "patche", candidates);
            Assert.Contains("'patch'", msg);
            Assert.Contains("Did you mean", msg);
            Assert.Contains("Allowed:", msg);
        }

        [Fact]
        public void FormatSuggestionMessage_OmitsSuggestionWhenTooFar()
        {
            var candidates = new[] { "xml", "ops", "patch" };
            string msg = DidYouMean.FormatSuggestionMessage("edit mode", "completely_wrong", candidates);
            Assert.DoesNotContain("Did you mean", msg);
            Assert.Contains("Allowed:", msg);
        }

        [Fact]
        public void Suggest_MatchesStemAndSynonyms()
        {
            var candidates = new[] { "pattern", "objectName", "maxResults", "timeoutMs" };
            Assert.Equal("objectName", DidYouMean.Suggest("objects", candidates));
            Assert.Equal("maxResults", DidYouMean.Suggest("limit", candidates));
        }
    }
}
