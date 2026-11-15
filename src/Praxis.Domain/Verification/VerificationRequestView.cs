namespace Praxis.Domain.Verification;

public sealed record VerificationRequestView(
    Guid RequestId, string Reason, string Status, string? ProviderId, DateTimeOffset CreatedUtc,
    string? FailureCode, string? ModelName, int? InputTokens, int? OutputTokens, string Payload,
    IReadOnlyList<VerificationItemView> Items, int? ItemCount = null);
