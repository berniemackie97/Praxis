namespace Praxis.Domain.Finance;

/// <summary>A persisted row from an eligible, counted document, with its evidence and model annotation when one exists.</summary>
public sealed record StoredTransaction(ParsedTransaction Row, FinancialAccount Account, FinancialEvidence Evidence, TransactionAnnotation? Annotation);
