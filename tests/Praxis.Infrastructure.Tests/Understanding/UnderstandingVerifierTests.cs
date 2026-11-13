using Praxis.Application.Understanding;
using Praxis.Application.Understanding.Profiles;
using Praxis.Domain.Understanding;
using Xunit;

namespace Praxis.Infrastructure.Tests.Understanding;

/// <summary>Nothing a model says is kept unless code finds it in the document (ADR 0044 §4).</summary>
public sealed class UnderstandingVerifierTests
{
    private const string Text = """
        Kitchen renovation — meeting notes
        Ortega Renovations will install the cabinets. Alex Ortega
        (alex@ortega.example, 206-555-0142) leads the crew.
        Site visit on Nov 20, 2026 at 2:30 pm at 14 Pine Street.
        Invoice 2026-118 for $3,480.00 is due December 1, 2026.
        """;

    private static readonly string[] Kinds = [RecordKinds.Event, RecordKinds.Deadline, RecordKinds.Contact];

    [Fact]
    public void QuotesAreFoundAcrossLineBreaksAndCase()
    {
        (int offset, int length) = EvidenceLocator.Find(Text, 0, Text.Length, "install the CABINETS. alex   ortega")!.Value;
        Assert.Equal("install the cabinets. Alex Ortega", Text.Substring(offset, length));
        Assert.Null(EvidenceLocator.Find(Text, 0, Text.Length, "install the countertops"));
    }

    [Fact]
    public void VerifiedItemsAreKeptAndInventionsAreRejected()
    {
        const string answer = """
            {"summary":"Meeting notes on the kitchen renovation.","documentType":"meeting notes",
             "topics":[{"name":"Kitchen renovation","quote":"Kitchen renovation — meeting notes"}],
             "entities":[
               {"name":"Ortega Renovations","type":"organization","quote":"Ortega Renovations will install the cabinets."},
               {"name":"Alex Ortega","type":"person","quote":"Alex Ortega"},
               {"name":"Acme Corp","type":"organization","quote":"Acme Corp signed the contract"}],
             "relations":[{"subject":"Alex Ortega","predicate":"member_of","object":"Ortega Renovations","quote":"Ortega Renovations will install the cabinets. Alex Ortega"}],
             "records":[
               {"kind":"event","title":"Site visit","start":"2026-11-20T14:30","location":"14 Pine Street","quote":"Site visit on Nov 20, 2026 at 2:30 pm at 14 Pine Street."},
               {"kind":"deadline","title":"Invoice 2026-118","due":"2026-12-01","amount":"3480.00","currency":"USD","quote":"Invoice 2026-118 for $3,480.00 is due December 1, 2026."},
               {"kind":"contact","name":"Alex Ortega","email":"alex@ortega.example","phone":"206-555-0142","quote":"Alex Ortega\n(alex@ortega.example, 206-555-0142)"},
               {"kind":"event","title":"Kickoff","start":"2026-11-21","quote":"Site visit on Nov 20, 2026 at 2:30 pm"}]}
            """;
        UnderstandingDraft draft = Check(answer)!;

        Assert.Equal(2, draft.Rejected);
        Assert.True(draft.Acceptable);
        Assert.Equal(["Kitchen renovation", "Ortega Renovations", "Alex Ortega", "member_of"], draft.Candidates.Select(candidate => candidate.Value));
        IntakeRecord visit = Assert.Single(draft.Records, record => record.Kind == RecordKinds.Event);
        Assert.Equal("2026-11-20T14:30", visit.Fields["start"]);
        Assert.Equal("2026-11-20T14:30", visit.SortKey);
        IntakeRecord invoice = Assert.Single(draft.Records, record => record.Kind == RecordKinds.Deadline);
        Assert.Equal(("2026-12-01", "348000"), (invoice.Fields["due"], invoice.Fields["amount_minor"]));
        Assert.Equal("alex@ortega.example", Assert.Single(draft.Records, record => record.Kind == RecordKinds.Contact).Fields["email"]);
    }

    [Fact]
    public void MostlyUnverifiableAnswersAreNotAcceptable()
    {
        UnderstandingDraft draft = Check("""
            {"summary":"Notes.","documentType":"notes","topics":[],"relations":[],"records":[],
             "entities":[{"name":"Zed","type":"person","quote":"Zed called"},{"name":"Yan","type":"person","quote":"Yan wrote"},
                         {"name":"Alex Ortega","type":"person","quote":"Alex Ortega"}]}
            """)!;
        Assert.False(draft.Acceptable);
        Assert.Null(Check("not json"));
    }

    [Theory]
    [InlineData("Due 12/01/2026", 2026, 12, 1)]
    [InlineData("due Dec 1st", 2026, 12, 1)]
    [InlineData("on 1 December 2026", 2026, 12, 1)]
    [InlineData("2026-12-01 settlement", 2026, 12, 1)]
    public void DatesMustBePrintedInSomeSpelling(string evidence, int year, int month, int day)
    {
        Assert.True(PrintedValues.HasDate(evidence, new DateOnly(year, month, day)));
        Assert.False(PrintedValues.HasDate(evidence, new DateOnly(year, month, day).AddDays(1)));
    }

    [Fact]
    public void LongDocumentsSplitAtPageBreaksAndReportWhatWasNotCovered()
    {
        string text = string.Join("\n\f\n", Enumerable.Range(1, 6).Select(page => new string((char)('a' + page), 400)));
        (IReadOnlyList<DocumentChunk> chunks, bool complete) = DocumentChunker.Split(text, 1_000, 2);
        Assert.Equal(2, chunks.Count);
        Assert.False(complete);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 1_000));
        Assert.Equal('\f', text[chunks[1].Offset - 2]);
        Assert.True(DocumentChunker.Split(text).Complete);
    }

    private static UnderstandingDraft? Check(string answer) =>
        UnderstandingVerifier.Check(answer, Text, new DocumentChunk(0, 0, Text.Length), RecordProfiles.Default, Kinds, "strong", "claude-code");
}
