using Praxis.Domain.Originals;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>The source behind a leased triage or understanding job, with how it was read and triaged so far.</summary>
public sealed record LeasedUnderstandingSource(
    Guid SourceId, string SourceKind, string Locator, OriginalObject Original, DocumentFacts Facts, DocumentProfile? Profile)
{
    public string FileName => Path.GetFileName(Locator);

    /// <summary>A stable key for structures that need a fallback identity, such as an unnamed account.</summary>
    public string SourceKey => "src-" + SourceId.ToString("N")[..12];
}
