using Praxis.Domain.Models;

namespace Praxis.Application.Policy;

/// <summary>
/// How an intake step ended: accepted (verified output from a tier), partial (no tier fully passed
/// verification, so only the verified subset of the best answer is kept), unverified (models ran but
/// nothing passed), held for an owner decision, index-only or skipped by policy. Token counts sum
/// every attempt made for the step.
/// </summary>
public sealed record LadderResult<T>(
    string Status, T? Value, string? Tier, string? ProviderId, ModelIdentity? Identity, string Reason, int InputTokens = 0, int OutputTokens = 0)
    where T : class
{
    public const string Accepted = "accepted";
    public const string Partial = "partial";
    public const string Unverified = "unverified";
    public const string Held = "held";
    public const string IndexOnly = "index_only";
    public const string Skipped = "skipped";
}
