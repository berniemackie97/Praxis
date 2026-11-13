using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

public sealed record VerificationDisclosureSourcesResponse(IReadOnlyList<VerificationDisclosureSource> Sources);
