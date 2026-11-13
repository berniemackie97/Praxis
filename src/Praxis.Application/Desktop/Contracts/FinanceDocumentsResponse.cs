using Praxis.Domain.Finance;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Recognition outcomes of financial sources, including documents that could not be proven.</summary>
public sealed record FinanceDocumentsResponse(IReadOnlyList<FinancialDocumentSummary> Documents);
