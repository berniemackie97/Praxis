using Praxis.Domain.Knowledge;
using Xunit;

namespace Praxis.Domain.Tests.Knowledge;

public sealed class RuntimeGlueTests
{
    [Theory]
    [InlineData("strlen")]
    [InlineData("_snprintf")]
    [InlineData("memcpy")]
    [InlineData("operator new")]
    [InlineData("__EH_prolog")]
    [InlineData("__chkstk")]
    [InlineData("_CxxThrowException")]
    public void RuntimeAndCompilerSupportRoutinesAreGlue(string name) => Assert.True(RuntimeGlue.IsGlue(name));

    [Theory]
    [InlineData("CGameMap_LoadMapFileFromRelativePath")]
    [InlineData("CharBuffer_AssignRange")]
    [InlineData("_private")]
    [InlineData("__")]
    [InlineData("Strlen")]
    [InlineData("graphic.dll")]
    public void OwnerCodeAndOrdinaryNamesAreNotGlue(string name) => Assert.False(RuntimeGlue.IsGlue(name));
}
