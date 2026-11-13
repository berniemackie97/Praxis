using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>The counted rows of the leased revision and the source they came from.</summary>
public sealed record LeasedFinancialRows(Guid SourceId, IReadOnlyList<ParsedTransaction> Rows);
