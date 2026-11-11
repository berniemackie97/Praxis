using Praxis.Domain.Enrichment;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>A source's proposals with the heading and semantic producers' outcomes.</summary>
public sealed record TopicsResponse(IReadOnlyList<EnrichmentProposal> Proposals, string Outcome, string SemanticOutcome);
