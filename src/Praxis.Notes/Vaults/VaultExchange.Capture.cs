using System.Text;
using Praxis.Notes.Model;

namespace Praxis.Notes.Vaults;

public static partial class VaultExchange
{
    private const int MaxNotes = 10_000;
    private const int MaxAttachments = 10_000;
    private const int MaxDirectories = 20_000;
    private const int MaxDepth = 64;
    private const int MaxNoteBytes = 8 * 1024 * 1024;
    private const int MaxAttachmentBytes = 32 * 1024 * 1024;
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static KnowledgeCollection Capture(string vaultDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        string root = Path.GetFullPath(vaultDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        CheckLink(root);
        List<NoteRecord> notes = new();
        List<AttachmentRecord> attachments = new();
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> directories = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        Walk(root, 0);
        return files.Any(file => directories.ContainsKey(file))
            ? throw new InvalidDataException("A vault file conflicts with a directory path.")
            : new KnowledgeCollection(notes, attachments, []);

        void Walk(string directory, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("The vault directory nesting exceeds the supported limit.");
            }

            string[] entries = Directory.EnumerateFileSystemEntries(directory).Take(MaxNotes + MaxAttachments + MaxDirectories + 1).ToArray();
            if (entries.Length > MaxNotes + MaxAttachments + MaxDirectories)
            {
                throw new InvalidDataException("The vault directory contains too many entries.");
            }

            Array.Sort(entries, StringComparer.Ordinal);
            if (depth > 0 && entries.Length == 0)
            {
                throw new InvalidDataException("Empty vault directories cannot be represented by the archive.");
            }

            foreach (string entry in entries)
            {
                CheckLink(entry);
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                try
                {
                    PortablePath.Validate(relative);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The vault contains a nonportable path.", exception);
                }

                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                {
                    if (directories.Count >= MaxDirectories || directories.TryGetValue(relative, out string? prior) && prior != relative)
                    {
                        throw new InvalidDataException("The vault contains conflicting or excessive directories.");
                    }

                    directories.TryAdd(relative, relative);
                    Walk(entry, depth + 1);
                    continue;
                }

                if (!files.Add(relative) || files.Count > MaxNotes + MaxAttachments)
                {
                    throw new InvalidDataException("The vault contains duplicate or excessive file paths.");
                }

                bool isNote = relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                int maximum = isNote ? MaxNoteBytes : MaxAttachmentBytes;
                using FileStream input = new(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > maximum || input.Length < 0 || (totalBytes += input.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("The vault file or total content exceeds archive limits.");
                }

                byte[] bytes = new byte[(int)input.Length];
                input.ReadExactly(bytes);
                if (input.ReadByte() != -1)
                {
                    throw new InvalidDataException("A vault file changed during capture.");
                }

                if (isNote)
                {
                    if (notes.Count == MaxNotes)
                    {
                        throw new InvalidDataException("The vault contains too many notes.");
                    }

                    string markdown;
                    try
                    {
                        markdown = Utf8.GetString(bytes);
                    }
                    catch (DecoderFallbackException exception)
                    {
                        throw new InvalidDataException("A Markdown note is not valid UTF-8.", exception);
                    }

                    notes.Add(NoteRecord.Create(relative, markdown));
                }
                else
                {
                    if (attachments.Count == MaxAttachments)
                    {
                        throw new InvalidDataException("The vault contains too many attachments.");
                    }

                    attachments.Add(AttachmentRecord.Create(relative, bytes));
                }
            }
        }
    }

    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Symbolic links and reparse points cannot be captured or restored.");
        }
    }
}
