namespace Praxis.Knowledge;

public sealed class KnowledgeCollection
{
    public KnowledgeCollection(
        IEnumerable<NoteRecord> notes,
        IEnumerable<AttachmentRecord> attachments,
        IEnumerable<AttachmentReference> references,
        IEnumerable<AttachmentReference>? vaultReferences = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(references);

        NoteRecord[] noteArray = notes.ToArray();
        AttachmentRecord[] attachmentArray = attachments.ToArray();
        AttachmentReference[] referenceArray = references.ToArray();
        AttachmentReference[]? vaultReferenceArray = vaultReferences?.ToArray();

        HashSet<Guid> noteIds = new HashSet<Guid>();
        HashSet<Guid> attachmentIds = new HashSet<Guid>();
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (noteArray.Any(note => !noteIds.Add(note.Id) || !paths.Add(note.Current.Path)))
        {
            throw new ArgumentException("The collection contains duplicate or invalid notes.", nameof(notes));
        }

        if (attachmentArray.Any(attachment => !attachmentIds.Add(attachment.Id) || !paths.Add(attachment.Path)))
        {
            throw new ArgumentException("The collection contains duplicate or invalid attachments.", nameof(attachments));
        }

        ValidateReferences(referenceArray, nameof(references));
        if (vaultReferenceArray is not null)
        {
            ValidateReferences(vaultReferenceArray, nameof(vaultReferences));
        }

        void ValidateReferences(AttachmentReference[] values, string parameter)
        {
            HashSet<AttachmentReference> links = new HashSet<AttachmentReference>();
            if (values.Any(reference => !noteIds.Contains(reference.NoteId) || !attachmentIds.Contains(reference.AttachmentId) || !links.Add(reference)))
            {
                throw new ArgumentException("An attachment reference must resolve exactly once.", parameter);
            }
        }

        Notes = Array.AsReadOnly(noteArray);
        Attachments = Array.AsReadOnly(attachmentArray);
        References = Array.AsReadOnly(referenceArray);
        VaultReferences = vaultReferenceArray is null ? null : Array.AsReadOnly(vaultReferenceArray);
    }

    public IReadOnlyList<NoteRecord> Notes { get; }

    public IReadOnlyList<AttachmentRecord> Attachments { get; }

    public IReadOnlyList<AttachmentReference> References { get; }

    public IReadOnlyList<AttachmentReference>? VaultReferences { get; }
}
