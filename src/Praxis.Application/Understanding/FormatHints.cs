using Praxis.Application.Documents;
using Praxis.Domain.Policy;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Triage decided by format alone, without a model, where the format is unambiguous: published
/// financial interchange, source code and subtitles. Everything else is triaged by the local tier.
/// </summary>
public static class FormatHints
{
    public static DocumentProfile? For(DocumentFacts facts) => facts.Format switch
    {
        DocumentFormats.FinancialInterchange => new DocumentProfile(DataCategories.Financial, "financial export", null, null, [RecordKinds.Transaction]),
        DocumentFormats.Code => new DocumentProfile(DataCategories.Code, "source code", null, null, []),
        DocumentFormats.Subtitles => new DocumentProfile(DataCategories.Media, "subtitles", null, null, [RecordKinds.Event]),
        _ => null
    };

    /// <summary>The profile used when nothing better is known: strictest category, generic record kinds.</summary>
    public static DocumentProfile Unknown(string? category = null) =>
        new(category ?? DataCategories.Unclassified, null, null, null, [RecordKinds.Event, RecordKinds.Deadline, RecordKinds.Contact]);
}
