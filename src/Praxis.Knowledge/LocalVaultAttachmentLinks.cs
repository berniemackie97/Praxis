using System.Text.RegularExpressions;

namespace Praxis.Knowledge;

public sealed record VaultLinkProblem(string NotePath, string Target, string Reason);

public sealed record VaultLinkReport(
    IReadOnlyList<AttachmentReference> References, IReadOnlyList<VaultLinkProblem> Problems);

public static class LocalVaultAttachmentLinks
{
    private static readonly Regex WikiEmbed = new(
        @"!\[\[([^\]\r\n]+)\]\]", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownEmbed = new(
        @"!\[[^\]\r\n]*\]\(([^()\r\n]+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static VaultLinkReport Inspect(KnowledgeCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        Dictionary<string, AttachmentRecord> byPath = collection.Attachments.ToDictionary(
            item => item.Path, StringComparer.OrdinalIgnoreCase);
        ILookup<string, AttachmentRecord> byName = collection.Attachments.ToLookup(
            item => item.Path[(item.Path.LastIndexOf('/') + 1)..],
            StringComparer.OrdinalIgnoreCase);
        HashSet<AttachmentReference> references = new HashSet<AttachmentReference>();
        List<VaultLinkProblem> problems = new List<VaultLinkProblem>();
        int scannedEmbeds = 0;

        foreach (NoteRecord note in collection.Notes)
        {
            char fence = '\0';
            foreach (string line in note.Current.Markdown.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (fence == '\0' && (trimmed.StartsWith("~~~") || trimmed.StartsWith("\u0060\u0060\u0060")))
                {
                    fence = trimmed[0];
                    continue;
                }

                if (fence != '\0')
                {
                    if (trimmed.Length >= 3 && trimmed[0] == fence &&
                        trimmed[1] == fence && trimmed[2] == fence)
                    {
                        fence = '\0';
                    }

                    continue;
                }

                string visible = HideInlineCode(line);
                foreach (Match match in WikiEmbed.Matches(visible))
                {
                    string target = match.Groups[1].Value.Split('|', 2)[0].Split('#', 2)[0];
                    CheckLimit();
                    if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        !Path.HasExtension(target))
                    {
                        continue;
                    }

                    Resolve(note, target, wiki: true);
                }

                foreach (Match match in MarkdownEmbed.Matches(visible))
                {
                    CheckLimit();
                    string value = match.Groups[1].Value.Trim();
                    if (Uri.TryCreate(value, UriKind.Absolute, out _) ||
                        value.StartsWith("//", StringComparison.Ordinal) ||
                        value.StartsWith('#'))
                    {
                        continue;
                    }

                    string target = value.Split('#', 2)[0].Split('?', 2)[0];
                    try
                    {
                        Resolve(note, Uri.UnescapeDataString(target), wiki: false);
                    }
                    catch (UriFormatException)
                    {
                        problems.Add(new VaultLinkProblem(note.Current.Path, value, "Invalid link encoding."));
                    }
                }
            }
        }

        return new VaultLinkReport(
            Array.AsReadOnly(references.OrderBy(item => item.NoteId)
                .ThenBy(item => item.AttachmentId).ToArray()),
            Array.AsReadOnly(problems.OrderBy(item => item.NotePath, StringComparer.Ordinal)
                .ThenBy(item => item.Target, StringComparer.Ordinal).ToArray()));

        void CheckLimit()
        {
            if (++scannedEmbeds > 100_000)
            {
                throw new InvalidDataException("The vault contains too many embedded links to audit.");
            }
        }

        void Resolve(NoteRecord note, string target, bool wiki)
        {
            string path = note.Current.Path;
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            HashSet<Guid> candidates = new HashSet<Guid>();
            HashSet<string> attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool relative = target.StartsWith("./", StringComparison.Ordinal) ||
                            target.StartsWith("../", StringComparison.Ordinal);

            if (relative || !wiki)
            {
                string parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                List<string> segments = new List<string>(parent.Length == 0 ? [] : parent.Split('/'));
                bool escaped = false;
                foreach (string segment in target.Split('/'))
                {
                    if (segment is "" or ".")
                    {
                        continue;
                    }

                    if (segment == "..")
                    {
                        if (segments.Count == 0)
                        {
                            escaped = true;
                            break;
                        }

                        segments.RemoveAt(segments.Count - 1);
                    }
                    else
                    {
                        segments.Add(segment);
                    }
                }

                if (!escaped)
                {
                    attempted.Add(string.Join('/', segments));
                }
                else
                {
                    problems.Add(new VaultLinkProblem(path, target, "Link escapes the vault root."));
                    return;
                }
            }

            if (!relative)
            {
                attempted.Add(target.TrimStart('/'));
            }

            foreach (string candidate in attempted)
            {
                if (byPath.TryGetValue(candidate, out AttachmentRecord? attachment))
                {
                    candidates.Add(attachment.Id);
                }
            }

            if (wiki && !target.Contains('/'))
            {
                foreach (AttachmentRecord attachment in byName[target])
                {
                    candidates.Add(attachment.Id);
                }
            }

            if (candidates.Count == 1)
            {
                references.Add(new AttachmentReference(note.Id, candidates.Single()));
            }
            else if (candidates.Count > 1)
            {
                problems.Add(new VaultLinkProblem(path, target, "Multiple attachments match."));
            }
            else
            {
                problems.Add(new VaultLinkProblem(path, target, "No attachment matches."));
            }
        }
    }

    public static KnowledgeCollection Track(KnowledgeCollection collection)
    {
        VaultLinkReport report = Inspect(collection);
        if (report.Problems.Count != 0)
        {
            VaultLinkProblem problem = report.Problems[0];
            throw new InvalidDataException(
                $"Attachment link audit found {report.Problems.Count} problem(s); " +
                $"{problem.NotePath}: {problem.Target}: {problem.Reason}");
        }

        return new KnowledgeCollection(
            collection.Notes, collection.Attachments, collection.References, report.References);
    }

    private static string HideInlineCode(string line)
    {
        char[] characters = line.ToCharArray();
        for (int i = 0; i < characters.Length;)
        {
            if (characters[i] != '\u0060')
            {
                i++;
                continue;
            }

            int start = i;
            while (i < characters.Length && characters[i] == '\u0060')
            {
                i++;
            }

            int delimiterLength = i - start;
            int end = -1;
            for (int j = i; j < characters.Length; j++)
            {
                if (characters[j] != '\u0060')
                {
                    continue;
                }

                int runStart = j;
                while (j < characters.Length && characters[j] == '\u0060')
                {
                    j++;
                }

                if (j - runStart == delimiterLength)
                {
                    end = j;
                    break;
                }

                j--;
            }

            if (end == -1)
            {
                continue;
            }

            Array.Fill(characters, ' ', start, end - start);
            i = end;
        }

        return new string(characters);
    }
}
