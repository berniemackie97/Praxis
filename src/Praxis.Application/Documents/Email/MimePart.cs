namespace Praxis.Application.Documents.Email;

/// <summary>A decoded leaf of a MIME message: its media type, file name when an attachment, and content.</summary>
public sealed record MimePart(string MediaType, string? FileName, bool IsAttachment, byte[] Content, string Charset);
