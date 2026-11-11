using Praxis.Domain.Common;
using Xunit;

namespace Praxis.Domain.Tests.Common;

public sealed class PathScopeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "praxis-scope");

    [Fact]
    public void ContainsTheDirectoryAndItsDescendants()
    {
        Assert.True(PathScope.Contains(Root, Root));
        Assert.True(PathScope.Contains(Root, Path.Combine(Root, "notes", "a.md")));
    }

    [Fact]
    public void ExcludesSiblingsWithACommonPrefixAndParents()
    {
        Assert.False(PathScope.Contains(Root, Root + "-other"));
        Assert.False(PathScope.Contains(Root, Path.GetDirectoryName(Root)!));
    }
}
