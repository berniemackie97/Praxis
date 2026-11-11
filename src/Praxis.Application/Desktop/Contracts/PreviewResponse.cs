namespace Praxis.Application.Desktop.Contracts;

/// <summary>Bounded extracted text of one revision, or why none is available.</summary>
public sealed record PreviewResponse(string Outcome, string? Content, string? ReasonCode);
