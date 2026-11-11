using Praxis.Application.Inference;
using Praxis.Domain.Enrichment;
using Praxis.Infrastructure.Enrichment.Semantic;
using Xunit;

namespace Praxis.Infrastructure.Tests.Enrichment;

public sealed class SemanticEvidenceTests
{
    private const string Segment = "Praxis stores immutable originals.\nSearch uses SQLite FTS5\n  with BM25 ranking.";

    [Fact]
    public void ExactQuotesAreLocated() =>
        Assert.Equal((14, 19), EvidenceLocator.Find(Segment, "immutable originals"));

    [Fact]
    public void ReflowedWhitespaceMapsBackToTheOriginalSpan()
    {
        (int Offset, int Length)? span = EvidenceLocator.Find(Segment, "SQLite FTS5 with BM25");
        Assert.NotNull(span);
        Assert.Equal("SQLite FTS5\n  with BM25", Segment.Substring(span.Value.Offset, span.Value.Length));
    }

    [Theory]
    [InlineData("SQLite FTS6 with BM25")]
    [InlineData("immutable  copies")]
    [InlineData("ab")]
    [InlineData("   ")]
    public void ChangedOrTrivialQuotesAreNotFound(string quote) =>
        Assert.Null(EvidenceLocator.Find(Segment, quote));

    [Fact]
    public void OnlyGroundedItemsBecomeCandidatesWithAbsoluteOffsets()
    {
        const string json = """
            {"topics":[
               {"name":"Full-text  search","quote":"Search uses SQLite FTS5"},
               {"name":"Invented","quote":"this sentence is not in the text"}],
             "entities":[
               {"name":"SQLite","type":"technology","quote":"SQLite FTS5"},
               {"name":"Postgres","type":"technology","quote":"Search uses SQLite FTS5"},
               {"name":"BM25","type":"spaceship","quote":"BM25 ranking"},
               {"name":"Praxis","type":"project","quote":"Praxis stores"}]}
            """;

        (IReadOnlyList<ProposalCandidate> accepted, int rejected) = SemanticResponseReader.Read(json, Segment, segmentStart: 1_000);

        Assert.Equal(3, rejected);
        Assert.Equal(
            [
                new ProposalCandidate("topic", null, "Full-text search", 1_035, 23),
                new ProposalCandidate("entity", "technology", "SQLite", 1_047, 11),
                new ProposalCandidate("entity", "project", "Praxis", 1_000, 13)
            ],
            accepted);
    }

    [Fact]
    public void ItemsBeyondTheSchemaLimitsAreRejected()
    {
        string topics = string.Join(',', Enumerable.Repeat("""{"name":"Praxis","quote":"Praxis stores"}""", 7));
        (IReadOnlyList<ProposalCandidate> accepted, int rejected) = SemanticResponseReader.Read(
            $$"""{"topics":[{{topics}}],"entities":[]}""", Segment, 0);

        Assert.Equal(SemanticExtractionPrompt.MaximumTopics, accepted.Count);
        Assert.Equal(2, rejected);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"topics":[]}""")]
    [InlineData("""["topics","entities"]""")]
    public void MalformedOrOffSchemaAnswersAreInvalidResponses(string json)
    {
        InferenceException exception = Assert.Throws<InferenceException>(() => SemanticResponseReader.Read(json, Segment, 0));
        Assert.Equal(InferenceFailure.InvalidResponse, exception.Failure);
    }
}
