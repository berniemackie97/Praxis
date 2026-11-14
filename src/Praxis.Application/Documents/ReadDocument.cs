using Praxis.Application.Documents.Tables;
using Praxis.Domain.Finance;
using Praxis.Domain.Media;

namespace Praxis.Application.Documents;

/// <summary>
/// A document normalized for every downstream stage: readable text (pages separated by form
/// feeds, table rows as <c>a | b | c</c> lines), optional title and page count, and optional
/// structure that lets later stages work in code instead of tokens — table rows for delimited
/// and spreadsheet data, the typed transactions of published financial interchange formats, and the
/// timing of spoken text for recordings and online videos.
/// </summary>
public sealed record ReadDocument(
    string Format, string Text, int? Pages = null, string? Title = null,
    IReadOnlyList<DelimitedRow>? Rows = null, FinancialDocument? Financial = null, bool Truncated = false,
    IReadOnlyList<TimedSpan>? Timings = null)
{
    public const char PageBreak = '\f';
}
