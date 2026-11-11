namespace Praxis.Application.Text;

/// <summary>Verified extracted text handed to a downstream processor holding a lease.</summary>
public sealed record LeasedText(string Digest, string Content, string SourceKind, string Locator);
