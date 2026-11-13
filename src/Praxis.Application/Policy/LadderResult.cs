using Praxis.Domain.Models;

namespace Praxis.Application.Policy;

/// <summary>
/// How an intake step ended: accepted (verified output from a tier), unverified (models ran but
/// no answer passed verification), held for an owner decision, index-only or skipped by policy.
/// </summary>
public sealed record LadderResult<T>(string Status, T? Value, string? Tier, string? ProviderId, ModelIdentity? Identity, string Reason)
    where T : class
{
    public const string Accepted = "accepted";
    public const string Unverified = "unverified";
    public const string Held = "held";
    public const string IndexOnly = "index_only";
    public const string Skipped = "skipped";
}
