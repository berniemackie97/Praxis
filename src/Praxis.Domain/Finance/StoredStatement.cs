namespace Praxis.Domain.Finance;

/// <summary>A persisted statement period from an eligible document, with its account and evidence.</summary>
public sealed record StoredStatement(ParsedStatement Statement, FinancialAccount Account, FinancialEvidence Evidence);
