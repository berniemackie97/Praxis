using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

public sealed record VerificationDisclosureAuditResponse(IReadOnlyList<VerificationDisclosureEvent> Events);
