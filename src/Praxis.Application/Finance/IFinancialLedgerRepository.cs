using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>
/// The financial ledger: the transaction profile's typed projection of understood documents.
/// Rows are written by the understand stage in its own transaction, bound to the revision's
/// digest, and readable only while the source is active and the revision current. Revoking a
/// source purges its financial rows.
/// </summary>
public interface IFinancialLedgerRepository
{
    /// <summary>Rows of eligible documents whose statements count, with their annotations.</summary>
    IReadOnlyList<StoredTransaction> ReadCountedTransactions();

    /// <summary>Statements of eligible documents that carry a closing balance and count toward totals.</summary>
    IReadOnlyList<StoredStatement> ReadCountedStatements();

    /// <summary>Accounts named by eligible documents, one row per document and account.</summary>
    IReadOnlyList<FinancialAccount> ReadAccounts();

    /// <summary>Recognition outcomes of current, active candidate sources, newest first.</summary>
    IReadOnlyList<FinancialDocumentSummary> ReadDocuments(int limit);
}
