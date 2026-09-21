using System;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #260: pattern instance resolution is generic over the installed
    // pattern registry (K2BTools, WorkWithPlus, ...) instead of WorkWithPlus only.
    public class PatternInstanceSelectionTests
    {
        private static readonly Guid WwpId = PatternRegistry.WorkWithPlusPatternId;
        private static readonly Guid EntityServicesId = new Guid("589d4b49-e3f9-4d49-aaf4-fad023028eb1");
        private static readonly Guid PromptId = new Guid("589d4b49-e3f9-4d49-aaf4-fad023028eb6");
        private static readonly Guid TrnFormId = new Guid("bcc946b1-3fe3-4980-8948-42574dc14209");
        private static readonly Guid TransactionTypeId = new Guid("1db606f2-af09-4cf9-a3b5-b481519d28f6");

        private static string Manifest(Guid id, string name, string template) =>
            "<Pattern Publisher=\"K2B\" Id=\"" + id + "\" Name=\"" + name + "\" Version=\"16.0.0.30486\">" +
            "<Definition><InstanceName>" + template + "</InstanceName>" +
            "<ParentObjects><ParentObject Type=\"Transaction\" /></ParentObjects></Definition></Pattern>";

        private static PatternRegistry Registry() => new PatternRegistry(new[]
        {
            PatternRegistry.ParseManifest(Manifest(EntityServicesId, "K2BEntityServices", "K2BEntityServices{0}"), "es.Pattern"),
            PatternRegistry.ParseManifest(Manifest(PromptId, "K2BPrompt", "K2BPrompt{0}"), "prompt.Pattern"),
            PatternRegistry.ParseManifest(Manifest(TrnFormId, "K2BTrnForm", "K2BTrnForm{0}"), "trnform.Pattern")
        });

        private static PatternInstanceCandidate Trn(string name) => new PatternInstanceCandidate(name, "Transaction", TransactionTypeId);
        private static PatternInstanceCandidate Es(string parent) => new PatternInstanceCandidate("K2BEntityServices" + parent, "K2BEntityServices", EntityServicesId);
        private static PatternInstanceCandidate Prompt(string parent) => new PatternInstanceCandidate("K2BPrompt" + parent, "K2BPrompt", PromptId);
        private static PatternInstanceCandidate TrnForm(string parent) => new PatternInstanceCandidate("K2BTrnForm" + parent, "K2BTrnForm", TrnFormId);
        private static PatternInstanceCandidate Wwp(string parent) => new PatternInstanceCandidate("WorkWithPlus" + parent, "WorkWithPlus", WwpId);

        [Fact]
        public void NoPatternId_WorkWithPlusChild_TakesPrecedenceOverK2BChildren()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Es("Customer"), Wwp("Customer"), Prompt("Customer") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("WorkWithPlusCustomer", sel.Selected.Candidate.Name);
            Assert.True(sel.Selected.Pattern.IsWorkWithPlus);
        }

        [Fact]
        public void NoPatternId_NamedWorkWithPlusInstance_WinsOverOtherWorkWithPlusChild()
        {
            var other = new PatternInstanceCandidate("WorkWithPlusLegacy", "WorkWithPlus", WwpId);
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { other, Wwp("Customer") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("WorkWithPlusCustomer", sel.Selected.Candidate.Name);
        }

        [Fact]
        public void NoPatternId_SingleK2BChild_IsSelected()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Trn("Unrelated"), Es("Customer") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("K2BEntityServicesCustomer", sel.Selected.Candidate.Name);
            Assert.Equal("K2BEntityServices", sel.Selected.Pattern.Name);
        }

        [Fact]
        public void NoPatternId_SeveralK2BChildren_IsAmbiguousWithEveryCandidate()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Es("Customer"), Prompt("Customer"), TrnForm("Customer") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Ambiguous, sel.Status);
            Assert.Null(sel.Selected);
            Assert.Equal(3, sel.Candidates.Count);
            Assert.Equal(
                new[] { "K2BEntityServices", "K2BPrompt", "K2BTrnForm" },
                sel.Candidates.Select(c => c.Pattern.Name).ToArray());

            var json = sel.ToDiagnostic("Customer");
            Assert.Equal("PatternInstanceAmbiguous", (string)json["code"]);
            Assert.Equal(3, json["candidates"].Count());
            Assert.Equal("K2BPromptCustomer", (string)json["candidates"][1]["name"]);
            Assert.Equal("K2BPrompt", (string)json["candidates"][1]["pattern"]);
        }

        [Fact]
        public void DuplicateCandidates_AreCountedOnce()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Es("Customer"), Es("Customer") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
        }

        [Fact]
        public void ExplicitPatternId_PicksTemplateNamedChild()
        {
            var stray = new PatternInstanceCandidate("K2BPromptOther", "K2BPrompt", PromptId);
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Es("Customer"), stray, Prompt("Customer"), Wwp("Customer") }, PromptId, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("K2BPromptCustomer", sel.Selected.Candidate.Name);
        }

        [Fact]
        public void ExplicitPatternId_FallsBackToAnyChildOfThatPattern()
        {
            var renamed = new PatternInstanceCandidate("CustomerServices", "K2BEntityServices", EntityServicesId);
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Prompt("Customer"), renamed }, EntityServicesId, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("CustomerServices", sel.Selected.Candidate.Name);
        }

        [Fact]
        public void ExplicitWorkWithPlusId_NeverResolvesAK2BInstance()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Es("Customer") }, WwpId, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.NotFound, sel.Status);
        }

        [Fact]
        public void RequestedObjectIsAnInstance_IsSelectedItself()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Es("Customer"), new[] { Prompt("Customer") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("K2BEntityServicesCustomer", sel.Selected.Candidate.Name);
            Assert.Equal(EntityServicesId, sel.Selected.Pattern.Id);
        }

        [Fact]
        public void RequestedInstance_MatchedByTypeGuid_WhenTypeNameDiffers()
        {
            var requested = new PatternInstanceCandidate("K2BTrnFormCustomer", "Trn Form", TrnFormId);
            var sel = PatternAnalysisService.SelectPatternInstance(requested, null, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("K2BTrnForm", sel.Selected.Pattern.Name);
        }

        [Fact]
        public void RequestedInstance_WithDifferentPatternId_IsMismatch()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Es("Customer"), null, WwpId, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.Mismatch, sel.Status);
            Assert.Null(sel.Selected);
            var json = sel.ToDiagnostic("K2BEntityServicesCustomer");
            Assert.Equal("PatternMismatch", (string)json["code"]);
            Assert.Equal("K2BEntityServices", (string)json["objectPattern"]);
            Assert.Equal("WorkWithPlus", (string)json["requestedPattern"]);
        }

        [Fact]
        public void NothingMatches_IsNotFound()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Trn("Other") }, null, Registry());

            Assert.Equal(PatternInstanceSelectionStatus.NotFound, sel.Status);
            Assert.Null(sel.Selected);
            Assert.Null(sel.ToDiagnostic("Customer"));
        }

        [Fact]
        public void NullRegistry_StillResolvesWorkWithPlus()
        {
            var sel = PatternAnalysisService.SelectPatternInstance(
                Trn("Customer"), new[] { Wwp("Customer") }, null, null);

            Assert.Equal(PatternInstanceSelectionStatus.Selected, sel.Status);
            Assert.Equal("WorkWithPlusCustomer", sel.Selected.Candidate.Name);
        }
        [Fact]
        public void OwnerMatches_RejectsInstanceOwnedByAnotherObject()
        {
            var owner = Guid.NewGuid();
            // A DataView homonym of the Transaction must not claim the Transaction's instance.
            Assert.False(PatternAnalysisService.OwnerMatches(Guid.NewGuid(), owner));
            Assert.True(PatternAnalysisService.OwnerMatches(owner, owner));
            // Unknown ownership stays accepted.
            Assert.True(PatternAnalysisService.OwnerMatches(null, owner));
            Assert.True(PatternAnalysisService.OwnerMatches(Guid.Empty, owner));
        }
    }
}
