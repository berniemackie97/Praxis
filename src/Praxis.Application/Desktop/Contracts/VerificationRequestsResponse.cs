using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Recent verification requests.</summary>
public sealed record VerificationRequestsResponse(IReadOnlyList<VerificationRequestView> Requests);
