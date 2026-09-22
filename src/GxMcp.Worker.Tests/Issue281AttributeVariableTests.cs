using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    // Issue #281: attribute-based variables (DataTypeString "Attribute:X") were
    // flattened to primitives and could not be restored; reads hid the binding.
    public class Issue281AttributeVariableTests
    {
        [Theory]
        [InlineData("Attribute:CttCar", "CttCar")]
        [InlineData("attribute:CttCar", "CttCar")]
        [InlineData("ATTRIBUTE:CttCar", "CttCar")]
        [InlineData("  Attribute:CttCar  ", "CttCar")]
        [InlineData("&Attribute:CttCar", "CttCar")]
        public void TryParseAttributeReference_AcceptsPrefixedForm(string input, string expected)
        {
            Assert.True(VariableInjector.TryParseAttributeReference(input, out string name));
            Assert.Equal(expected, name);
        }

        [Theory]
        [InlineData("CttCar")]
        [InlineData("Numeric(10)")]
        [InlineData("")]
        [InlineData("Attribute:")]
        public void TryParseAttributeReference_RejectsNonAttributeForm(string input)
        {
            Assert.False(VariableInjector.TryParseAttributeReference(input, out _));
        }

        [Fact]
        public void Resolver_AttributePrefix_ReturnsAttributeReference()
        {
            var r = VariableTypeResolver.Resolve("Attribute:CttCar");
            Assert.True(r.Recognized);
            Assert.Equal("AttributeReference", r.CanonicalType);
            Assert.Equal("CttCar", r.AttributeName);
        }

        [Fact]
        public void Resolver_BareName_StillResolvesAsDomainReference()
        {
            // Bare names stay on the legacy Domain/SDT/BC path; the explicit
            // basedOnAttribute slot (or the Attribute: prefix) selects attributes.
            var r = VariableTypeResolver.Resolve("CttCar");
            Assert.True(r.Recognized);
            Assert.Equal("DomainReference", r.CanonicalType);
        }

        [Fact]
        public void ExtractOriginalTypeName_PreservesAttributeToken()
        {
            string dump = "&cttcar : Attribute:CttCar\n&other : Numeric(10)";
            Assert.Equal("Attribute:CttCar", Services.WriteService.ExtractOriginalTypeNameFromDump(dump, "cttcar"));
        }

        [Theory]
        [InlineData("CttCar", "Attribute:CttCar")]
        [InlineData("Attribute:CttCar", "Attribute:CttCar")]
        [InlineData("&CttCar", "Attribute:CttCar")]
        public void RequestedTypeForVerify_NormalizesAttributeRequests(string basedOnAttribute, string expected)
        {
            Assert.Equal(expected, Services.WriteService.RequestedTypeForVerify(null, null, basedOnAttribute));
        }

        [Fact]
        public void RequestedTypeForVerify_PrefersAttributeOverBasedOnAndType()
        {
            Assert.Equal("Attribute:CttCar",
                Services.WriteService.RequestedTypeForVerify("Numeric(10)", "MyDomain", "CttCar"));
        }

        [Fact]
        public void NativeDomainBinding_MatchingNameWithoutKeys_IsAccepted()
        {
            Assert.True(VariableInjector.IsNativeDomainBindingParts(
                "MyDomain", null, null, "mydomain", null, null, out var failure));
            Assert.Null(failure);
        }

        [Fact]
        public void NativeDomainBinding_NameMismatch_IsRejected()
        {
            Assert.False(VariableInjector.IsNativeDomainBindingParts(
                "MyDomain", null, null, "OtherDomain", null, null, out var failure));
            Assert.Contains("MyDomain", failure);
        }

        [Fact]
        public void NativeDomainBinding_MatchingNameAndKey_IsAccepted()
        {
            var type = System.Guid.NewGuid();
            Assert.True(VariableInjector.IsNativeDomainBindingParts(
                "MyDomain", type, 17, "MyDomain", type, 17, out var failure));
            Assert.Null(failure);
        }

        [Fact]
        public void NativeDomainBinding_MatchingNameButDifferentKey_IsRejected()
        {
            // Homonymous Domains in different Modules share the name but not the key.
            var type = System.Guid.NewGuid();
            Assert.False(VariableInjector.IsNativeDomainBindingParts(
                "MyDomain", type, 17, "MyDomain", type, 18, out var failure));
            Assert.Contains("homonymous", failure);
        }

        [Fact]
        public void NativeDomainBinding_MatchingNameWithOneKeyMissing_KeepsLegacyNameBehavior()
        {
            var type = System.Guid.NewGuid();
            Assert.True(VariableInjector.IsNativeDomainBindingParts(
                "MyDomain", type, 17, "MyDomain", null, null, out var failure));
            Assert.Null(failure);
        }

        [Fact]
        public void NativeObjectBinding_MatchingGuidAndKind_IsAccepted()
        {
            var guid = System.Guid.NewGuid();
            Assert.True(VariableInjector.IsNativeObjectBindingParts(
                guid, "SDT", guid, "sdt", out var failure));
            Assert.Null(failure);
        }

        [Fact]
        public void NativeObjectBinding_GuidMismatch_IsRejected()
        {
            Assert.False(VariableInjector.IsNativeObjectBindingParts(
                System.Guid.NewGuid(), "SDT", System.Guid.NewGuid(), "SDT", out var failure));
            Assert.Contains("not the requested", failure);
        }

        [Fact]
        public void NativeObjectBinding_MissingGuid_IsRejected()
        {
            // A dropped binding flattens to a primitive with no object reference.
            Assert.False(VariableInjector.IsNativeObjectBindingParts(
                System.Guid.NewGuid(), "BusinessComponent", null, null, out var failure));
            Assert.Contains("not the requested", failure);
        }

        [Fact]
        public void NativeObjectBinding_KindMismatch_IsRejected()
        {
            var guid = System.Guid.NewGuid();
            Assert.False(VariableInjector.IsNativeObjectBindingParts(
                guid, "BusinessComponent", guid, "Transaction", out var failure));
            Assert.Contains("kind", failure);
        }
    }
}
