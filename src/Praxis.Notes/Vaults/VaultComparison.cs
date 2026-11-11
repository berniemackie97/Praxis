namespace Praxis.Notes.Vaults;

public sealed record VaultComparison(int MatchedFiles, IReadOnlyList<VaultFileDifference> Differences)
{
    public bool IsMatch => Differences.Count == 0;
}
