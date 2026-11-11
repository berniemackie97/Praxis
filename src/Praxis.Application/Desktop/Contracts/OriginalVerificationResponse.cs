namespace Praxis.Application.Desktop.Contracts;

/// <summary>Confirmation that an original matched its recorded digest.</summary>
public sealed record OriginalVerificationResponse(bool Verified);
