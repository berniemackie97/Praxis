namespace Praxis.Application.Knowledge;

/// <summary>What one bounded reconsolidation pass did and how many entities still wait.</summary>
public sealed record ReconsolidationPass(int Examined, int Applied, int Suggested, int Remaining);
