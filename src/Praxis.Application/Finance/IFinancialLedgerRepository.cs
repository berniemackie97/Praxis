using Praxis.Domain.Finance;
using Praxis.Domain.Models;
using Praxis.Domain.Processing;

namespace Praxis.Application.Finance;

/// <summary>
/// Typed financial facts derived from immutable originals. Results are committed atomically with
/// their job's terminal state, bound to the revision's digest, and readable only while the
/// source is active and the revision current. Revoking a source purges its financial rows.
/// </summary>
public interface IFinancialLedgerRepository
{
    /// <summary>The source of a leased job, or null when the lease is no longer held or the source was revoked.</summary>
    LeasedFinancialSource? ReadLeasedSource(ProcessingLease lease);

    /// <summary>
    /// Stores a recognized document and completes the job in one transaction, scheduling the
    /// annotation stage. <paramref name="model"/> and <paramref name="tier"/> identify the model
    /// whose verified output produced it, or are null when code read a published format.
    /// </summary>
    bool Commit(ProcessingLease lease, FinancialDocument document, ModelIdentity? model, string? tier);

    /// <summary>Records why a source is not usable financial evidence and closes its job as unsupported.</summary>
    bool CommitUnsupported(ProcessingLease lease, string reasonCode);

    /// <summary>The counted rows of the leased revision for the annotation stage, or null when the lease is not held.</summary>
    LeasedFinancialRows? ReadLeasedTransactions(ProcessingLease lease);

    /// <summary>Replaces the revision's annotations and completes the annotation job in one transaction.</summary>
    bool CommitAnnotations(ProcessingLease lease, IReadOnlyList<TransactionAnnotation> annotations);

    /// <summary>Rows of eligible documents whose statements count, with their annotations.</summary>
    IReadOnlyList<StoredTransaction> ReadCountedTransactions();

    /// <summary>Statements of eligible documents that carry a closing balance and count toward totals.</summary>
    IReadOnlyList<StoredStatement> ReadCountedStatements();

    /// <summary>Accounts named by eligible documents, one row per document and account.</summary>
    IReadOnlyList<FinancialAccount> ReadAccounts();

    /// <summary>Recognition outcomes of current, active candidate sources, newest first.</summary>
    IReadOnlyList<FinancialDocumentSummary> ReadDocuments(int limit);
}
