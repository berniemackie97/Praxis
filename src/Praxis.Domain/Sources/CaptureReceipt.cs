namespace Praxis.Domain.Sources;

public sealed record CaptureReceipt(
    string RequestKey, int Revision, string? Route, string? AuthorityBasis,
    string? Scope, DateTimeOffset? RecordedUtc);
