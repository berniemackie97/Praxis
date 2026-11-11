namespace Praxis.Notes.Attachments;

public sealed record AttachmentTransferStatus(string Sha256, int Length, int Uploaded);
