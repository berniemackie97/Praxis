namespace Praxis.Domain.Security;

public sealed record DeviceApprovalChallenge(Guid ChallengeId, Guid DeviceId, byte[] Message,
    DateTimeOffset ExpiresUtc);
