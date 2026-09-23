using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class VariableDeclarationParserTests
    {
        [Fact]
        public void TryParse_PreservesModuleQualifiedSdtType()
        {
            Assert.True(VariableDeclarationParser.TryParse(
                "&GridState : WWPGridState, WorkWithPlus_Web",
                out var declaration));

            Assert.Equal("GridState", declaration.Name);
            Assert.Equal("WWPGridState, WorkWithPlus_Web", declaration.TypeName);
        }

        [Fact]
        public void TrySplitModuleQualifiedTypeName_ReturnsObjectAndModule()
        {
            Assert.True(VariableDeclarationParser.TrySplitModuleQualifiedTypeName(
                "WWPGridState, WorkWithPlus_Web",
                out var objectName,
                out var moduleName));

            Assert.Equal("WWPGridState", objectName);
            Assert.Equal("WorkWithPlus_Web", moduleName);
        }
    }
}
