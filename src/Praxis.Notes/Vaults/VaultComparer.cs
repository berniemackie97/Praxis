using Praxis.Notes.Archives;
using Praxis.Notes.Model;

namespace Praxis.Notes.Vaults;

public static class VaultComparer
{
    public static VaultComparison CompareArchive(string archivePath, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        KnowledgeCollection expected = NoteArchive.ReadCollection(stream);
        KnowledgeCollection actual = VaultExchange.Capture(directory);

        Dictionary<string, NoteRecord> actualNotes = actual.Notes.ToDictionary(note => note.Current.Path, StringComparer.Ordinal);
        Dictionary<string, AttachmentRecord> actualAttachments = actual.Attachments.ToDictionary(item => item.Path, StringComparer.Ordinal);
        List<VaultFileDifference> differences = new();
        int matched = 0;

        foreach (NoteRecord note in expected.Notes)
        {
            string path = note.Current.Path;
            if (!actualNotes.Remove(path, out NoteRecord? observed))
            {
                differences.Add(new VaultFileDifference(path, "missing"));
            }
            else if (!string.Equals(note.Current.Markdown, observed.Current.Markdown, StringComparison.Ordinal))
            {
                differences.Add(new VaultFileDifference(path, "changed"));
            }
            else
            {
                matched++;
            }
        }

        foreach (AttachmentRecord attachment in expected.Attachments)
        {
            if (!actualAttachments.Remove(attachment.Path, out AttachmentRecord? observed))
            {
                differences.Add(new VaultFileDifference(attachment.Path, "missing"));
            }
            else if (!attachment.ContentSpan.SequenceEqual(observed.ContentSpan))
            {
                differences.Add(new VaultFileDifference(attachment.Path, "changed"));
            }
            else
            {
                matched++;
            }
        }

        differences.AddRange(actualNotes.Keys.Select(path => new VaultFileDifference(path, "unexpected")));
        differences.AddRange(actualAttachments.Keys.Select(path => new VaultFileDifference(path, "unexpected")));
        differences.Sort((a, b) =>
        {
            int path = StringComparer.Ordinal.Compare(a.Path, b.Path);
            return path != 0 ? path : StringComparer.Ordinal.Compare(a.Reason, b.Reason);
        });

        return new VaultComparison(matched, Array.AsReadOnly(differences.ToArray()));
    }
}
