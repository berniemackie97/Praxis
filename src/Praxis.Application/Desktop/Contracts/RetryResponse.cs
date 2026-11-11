namespace Praxis.Application.Desktop.Contracts;

/// <summary>Whether failed processing was queued again.</summary>
public sealed record RetryResponse(bool Queued);
