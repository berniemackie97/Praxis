namespace Praxis.Domain.Models;

/// <summary>
/// One recorded model call: who was asked, why, about which data category, under which
/// disclosure decision, and what it cost. Remote invocations are the disclosure audit trail.
/// </summary>
public sealed record ModelInvocation(
    string Tier, string ProviderId, bool Remote, string Purpose, string Category, string DecisionReason,
    Guid? ItemId, int? Revision, string? ProcessorKey, string? ModelName, int InputTokens, int OutputTokens,
    string Outcome, DateTimeOffset StartedUtc);
