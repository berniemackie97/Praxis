namespace Praxis.Application.Desktop.Contracts;

/// <summary>The created verification request, or null when nothing needed verification.</summary>
public sealed record VerificationCreatedResponse(Guid? VerificationId);
