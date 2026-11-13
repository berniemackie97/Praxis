namespace Praxis.Domain.Finance;

/// <summary>Where a ledger value came from: the exact source revision, digest and row locator.</summary>
public sealed record FinancialEvidence(Guid SourceId, Guid ItemId, int Revision, string SourceSha256, string Locator, string Format, string SourceName);
