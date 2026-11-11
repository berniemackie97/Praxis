using Praxis.Domain.Text;

namespace Praxis.Domain.Enrichment;

/// <summary>A producer's earlier result for the same item, for reusing unchanged segments.</summary>
public sealed record PriorExtraction(
    string Content, IReadOnlyList<TextSegmentSpan> Segments, IReadOnlyList<ProposalCandidate> Candidates);
