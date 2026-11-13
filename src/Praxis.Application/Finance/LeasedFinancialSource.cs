using Praxis.Domain.Originals;

namespace Praxis.Application.Finance;

/// <summary>The active source and verified original behind a leased financial recognition job.</summary>
public sealed record LeasedFinancialSource(Guid SourceId, string SourceKind, string Locator, OriginalObject Original);
