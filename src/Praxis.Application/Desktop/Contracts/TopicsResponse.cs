using Praxis.Domain.Enrichment;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>
/// What intake found in a source, confirmed knowledge first, with the outcomes of the intake
/// understanding and the optional local enrichment that produced it.
/// </summary>
public sealed record TopicsResponse(IReadOnlyList<EnrichmentProposal> Proposals, string Outcome, string SemanticOutcome);
