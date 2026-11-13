using Praxis.Domain.Policy;

namespace Praxis.Application.Policy;

/// <summary>
/// Decides one disclosure from the current policy and the source's category. A source with no
/// category yet is <see cref="DataCategories.Unclassified"/>, the strictest category.
/// </summary>
public sealed class DisclosureGate(IPolicyRepository repository, TimeProvider clock)
{
    public (DisclosureDecision Decision, string Category) Decide(Guid? sourceId, string providerId, bool providerIsLocal, string purpose, string? knownCategory = null)
    {
        SourceCategory? category = sourceId is { } id ? repository.ReadCategory(id) : null;
        string resolved = category?.Category ?? knownCategory ?? DataCategories.Unclassified;
        return (DisclosureEvaluator.Decide(repository.ReadPolicy(), resolved, providerId, providerIsLocal, purpose,
            category?.LocalOnly ?? false, clock.GetUtcNow()), resolved);
    }
}
