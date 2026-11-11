namespace Praxis.Domain.Security;

/// <summary>A 30-second, single-use challenge for a specific device, security space and operation.</summary>
public sealed record DeviceChallenge(Guid ChallengeId, byte[] Message, DateTimeOffset ExpiresUtc);
