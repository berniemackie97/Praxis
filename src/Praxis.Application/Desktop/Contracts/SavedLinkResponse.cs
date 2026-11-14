namespace Praxis.Application.Desktop.Contracts;

/// <summary>A link the owner saved: its normalized URL and the link file Praxis wrote and captured.</summary>
public sealed record SavedLinkResponse(string Url, string FileName);
