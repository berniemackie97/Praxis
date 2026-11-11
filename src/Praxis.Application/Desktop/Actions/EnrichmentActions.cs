using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Enrichment;
using Praxis.Domain.Processing;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Reviewable topic, entity and relation proposals for a source.</summary>
public sealed class EnrichmentActions(IProposalRepository proposals) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("topics", fields =>
        {
            Guid sourceId = fields.RequireGuid("sourceId");
            return new TopicsResponse(
                proposals.List(sourceId, fields.IntegerOrDefault("limit", 40, 1, 100)),
                proposals.Outcome(sourceId, ProcessorKeys.MarkdownHeadings),
                proposals.Outcome(sourceId, ProcessorKeys.SemanticExtraction));
        }),
        DesktopAction.Of("topicReview", fields => new TopicReviewResponse(proposals.Review(
            fields.RequireGuid("proposalId"),
            fields.RequireString("decision", 16),
            fields.OptionalText("correction", 80, "The review correction is invalid."))))
    ];
}
