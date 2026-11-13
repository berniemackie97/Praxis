namespace Praxis.Domain.Verification;

public sealed record VerificationDisclosureSource(
    Guid SourceId, string Name, string Classification, IReadOnlyList<int> Ordinals);
