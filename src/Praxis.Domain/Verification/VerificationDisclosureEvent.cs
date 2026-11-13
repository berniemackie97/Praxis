namespace Praxis.Domain.Verification;

public sealed record VerificationDisclosureEvent(
    Guid EventId, Guid? RequestId, Guid? SourceId, string? ProviderId, string Actor,
    string Operation, string Result, string ReasonCode, DateTimeOffset OccurredUtc);
