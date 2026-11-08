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

        var noteArray = notes.ToArray();
        var attachmentArray = attachments.ToArray();
        var referenceArray = references.ToArray();
        var vaultReferenceArray = vaultReferences?.ToArray();

        var noteIds = new HashSet<Guid>();
        var attachmentIds = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in noteArray)
        {
            if (note is null || !noteIds.Add(note.Id) || !paths.Add(note.Current.Path))
            {
                throw new ArgumentException("The collection contains duplicate or invalid notes.", nameof(notes));
            }
        }

        foreach (var attachment in attachmentArray)
        {
            if (attachment is null || !attachmentIds.Add(attachment.Id) || !paths.Add(attachment.Path))
            {
                throw new ArgumentException("The collection contains duplicate or invalid attachments.", nameof(attachments));
            }
        }

        ValidateReferences(referenceArray, nameof(references));
        if (vaultReferenceArray is not null)
        {
            ValidateReferences(vaultReferenceArray, nameof(vaultReferences));
        }

        void ValidateReferences(AttachmentReference[] values, string parameter)
        {
            var links = new HashSet<AttachmentReference>();
            foreach (var reference in values)
            {
                if (reference is null ||
                    !noteIds.Contains(reference.NoteId) ||
                    !attachmentIds.Contains(reference.AttachmentId) ||
                    !links.Add(reference))
                {
                    throw new ArgumentException("An attachment reference must resolve exactly once.", parameter);
                }
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
