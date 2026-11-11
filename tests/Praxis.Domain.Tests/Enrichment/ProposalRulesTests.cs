using Praxis.Domain.Enrichment;
using Xunit;

namespace Praxis.Domain.Tests.Enrichment;

public sealed class ProposalRulesTests
{
    private const string Content = "Alex Rivera works on Praxis.";

    [Fact]
    public void AcceptsGroundedEntitiesAndRelations() =>
        ProposalRules.ValidateCandidates(Content,
        [
            new ProposalCandidate("entity", "person", "Alex Rivera", 0, 11),
            new ProposalCandidate("relation", null, "works_on", 0, Content.Length, "Alex Rivera", "Praxis")
        ]);

    [Theory]
    [InlineData("entity", "unknown-type", "Alex", 0, 4)]
    [InlineData("topic", null, "Praxis", 20, 20)]
    [InlineData("summary", null, "Praxis", 0, 6)]
    public void RejectsUnknownKindsTypesAndSpansOutsideTheText(string kind, string? type, string value, int offset, int length) =>
        Assert.Throws<ArgumentException>(() => ProposalRules.ValidateCandidates(Content,
            [new ProposalCandidate(kind, type, value, offset, length)]));

    [Fact]
    public void RejectsRunsAboveTheLimit() =>
        Assert.Throws<ArgumentException>(() => ProposalRules.ValidateCandidates(Content,
            Enumerable.Range(0, ProposalLimits.MaximumPerRun + 1).Select(_ => new ProposalCandidate("topic", null, "Praxis", 0, 4)).ToArray()));

    [Fact]
    public void OnlyCorrectionsCarryTrimmedReplacementText()
    {
        Assert.Equal("Praxis", ProposalRules.NormalizeReview("corrected", "  Praxis "));
        Assert.Null(ProposalRules.NormalizeReview("accepted", null));
        Assert.Throws<ArgumentException>(() => ProposalRules.NormalizeReview("accepted", "Praxis"));
        Assert.Throws<ArgumentException>(() => ProposalRules.NormalizeReview("corrected", " "));
        Assert.Throws<ArgumentException>(() => ProposalRules.NormalizeReview("maybe", null));
    }
}
