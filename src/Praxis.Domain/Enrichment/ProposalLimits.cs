namespace Praxis.Domain.Enrichment;

/// <summary>Bounds on what one producer run may propose.</summary>
public static class ProposalLimits
{
    public const int MaximumPerRun = 64;
    public const int MaximumValueLength = 120;
    public const int MaximumEvidenceLength = 240;
}
