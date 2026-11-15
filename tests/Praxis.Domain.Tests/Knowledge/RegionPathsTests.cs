using Praxis.Domain.Knowledge;
using Xunit;

namespace Praxis.Domain.Tests.Knowledge;

public sealed class RegionPathsTests
{
    private static readonly string[] Roots = ["/Inbox", "/Inbox/OpenConquerClient"];

    [Fact]
    public void LooseFilesGroupUnderTheirRoot() =>
        Assert.Equal(new RegionPlacement("Inbox/", "Loose files in Inbox", true, null, null),
            RegionPaths.Place("/Inbox/KITTY_CHEATSHEET.md", ["/Inbox"]));

    [Fact]
    public void TopFoldersAreRegionsAndSubfoldersAreChildren()
    {
        Assert.Equal(new RegionPlacement("Inbox/20_notes", "notes", false, "Inbox/20_notes/Game_Map", "Game Map"),
            RegionPaths.Place("/Inbox/20_notes/Game_Map/01_overview.md", ["/Inbox"]));
        Assert.Equal(new RegionPlacement("Inbox/20_notes", "notes", false, null, null),
            RegionPaths.Place("/Inbox/20_notes/README.md", ["/Inbox"]));
    }

    [Fact]
    public void ConfigurationFoldersDoNotBecomeRegions() =>
        Assert.Equal(new RegionPlacement("Inbox/Repo", "Repo", false, null, null),
            RegionPaths.Place("/Inbox/Repo/.github/workflows/ci.yml", ["/Inbox"]));

    [Fact]
    public void CodeContainersAreLookedThroughToTheirProjects() =>
        Assert.Equal(new RegionPlacement("Inbox/Repo", "Repo", false, "Inbox/Repo/src/Repo.Network", "Repo.Network"),
            RegionPaths.Place("/Inbox/Repo/src/Repo.Network/Client.cs", ["/Inbox"]));

    [Fact]
    public void TheDeepestRootWinsAndOutsidersAreUnplaced()
    {
        Assert.Equal("OpenConquerClient/docs", RegionPaths.Place("/Inbox/OpenConquerClient/docs/a/b.md", Roots)!.TopKey);
        Assert.Null(RegionPaths.Place("/Elsewhere/file.md", Roots));
        Assert.Null(RegionPaths.Place("/Inboxes/file.md", Roots));
    }

    [Theory]
    [InlineData("01_Formation_and_Governance", "Formation and Governance")]
    [InlineData("bradenton_resale_business_package", "bradenton resale business package")]
    [InlineData("2026", "2026")]
    [InlineData("OpenConquer.Client", "OpenConquer.Client")]
    public void LabelsReadLikeNames(string folder, string label) => Assert.Equal(label, RegionPaths.Label(folder));

    [Fact]
    public void IdsAreStableAndOpaque()
    {
        Assert.Equal(RegionPaths.Id("Inbox/20_notes"), RegionPaths.Id("Inbox/20_notes"));
        Assert.NotEqual(RegionPaths.Id("Inbox/20_notes"), RegionPaths.Id("Inbox/20_note"));
        Assert.Equal(32, RegionPaths.Id("x").Length);
    }
}
