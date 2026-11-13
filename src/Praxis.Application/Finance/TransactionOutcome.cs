using Praxis.Domain.Finance;
using Praxis.Domain.Models;

namespace Praxis.Application.Finance;

/// <summary>What the transaction profile produced for a document: a verified ledger document with annotations, or why not.</summary>
public sealed record TransactionOutcome(
    FinancialDocument? Document, IReadOnlyList<TransactionAnnotation> Annotations, string Status, string Reason,
    string? Tier, string? ProviderId, ModelIdentity? Identity, int InputTokens, int OutputTokens)
{
    public const string None = "none";

    public static TransactionOutcome NotApplicable { get; } = new(null, [], None, "not_financial", null, null, null, 0, 0);
}
