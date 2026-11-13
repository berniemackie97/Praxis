namespace Praxis.Domain.Finance;

/// <summary>
/// Model-derived understanding of one recorded row: merchant and category, with the tier and
/// model that produced it. Annotations never alter the row's provider facts.
/// </summary>
public sealed record TransactionAnnotation(int Ordinal, string Merchant, string Category, string Tier, string ProviderId, string? ModelName);
