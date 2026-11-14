namespace Praxis.Domain.Understanding;

/// <summary>What intake knows about one source revision: how it was read, triaged and understood.</summary>
public sealed record SourceUnderstanding(
    Guid ItemId, int Revision, string Format, int? Pages, string? Title, bool Truncated,
    string? Category, string? DocumentType, string? Language, string? Gist, string? Summary,
    string Status, string? Reason, string? Tier, string? ProviderId, string? Model,
    int VerifiedItems, int RejectedItems, int Records, int Transactions, long InputTokens, long OutputTokens, DateTimeOffset UpdatedUtc,
    string? Cascade = null);
