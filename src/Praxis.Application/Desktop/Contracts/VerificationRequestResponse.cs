using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>One verification request with its items.</summary>
public sealed record VerificationRequestResponse(VerificationRequestView? Request);
