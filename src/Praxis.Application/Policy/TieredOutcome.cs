using Praxis.Domain.Models;
using Praxis.Domain.Policy;

namespace Praxis.Application.Policy;

/// <summary>
/// The result of asking a tier: a completed answer, or why not — the tier is unconfigured or out
/// of allowance, disclosure is not allowed (with the decision), or the provider failed.
/// </summary>
public sealed record TieredOutcome(string Status, string? Content, ModelIdentity? Identity, string? ProviderId, DisclosureDecision? Decision, string? FailureCode)
{
    public const string Completed = "completed";
    public const string NotConfigured = "not_configured";
    public const string Disallowed = "disallowed";
    public const string Failed = "failed";

    public bool IsCompleted => Status == Completed;
}
