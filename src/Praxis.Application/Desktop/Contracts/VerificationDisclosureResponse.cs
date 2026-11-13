using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

public sealed record VerificationDisclosureSourcesResponse(IReadOnlyList<VerificationDisclosureSource> Sources);

public sealed record VerificationDisclosurePolicyResponse(bool Changed);

public sealed record VerificationDisclosureRevokedResponse(bool Revoked);

public sealed record VerificationDisclosureAuditResponse(IReadOnlyList<VerificationDisclosureEvent> Events);
