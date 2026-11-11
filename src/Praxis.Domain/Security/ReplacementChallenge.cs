namespace Praxis.Domain.Security;

public sealed record ReplacementChallenge(Guid EnrollmentId, byte[] Message, DateTimeOffset ExpiresUtc);
