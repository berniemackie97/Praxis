namespace Praxis.Domain.Policy;

/// <summary>
/// An owner-confirmed grant (<see cref="Allow"/> true) or denial for one category, provider and
/// purpose, optionally expiring. Denials beat grants and modes.
/// </summary>
public sealed record DisclosureRule(Guid RuleId, string Category, string ProviderId, string Purpose, bool Allow, DateTimeOffset? ExpiresUtc)
{
    public bool IsActive(DateTimeOffset now) => ExpiresUtc is null || ExpiresUtc > now;

    public void Validate()
    {
        if (RuleId == Guid.Empty || !DataCategories.IsValid(Category) || !DisclosurePurposes.All.Contains(Purpose, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 64)
        {
            throw new ArgumentException("The disclosure rule is invalid.");
        }
    }
}
