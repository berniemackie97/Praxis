using System.Text.RegularExpressions;

namespace Praxis.Application.Agents.Skills;

/// <summary>Composes the application-owned library and the owner's reviewed, local-only procedures.</summary>
public sealed partial class SkillLibrary(IEnumerable<SkillDocument> bundled, ISkillRepository repository, ISkillDocumentParser parser)
{
    private readonly IReadOnlyDictionary<string, SkillDocument> seeds = bundled.ToDictionary(item => item.Name, StringComparer.Ordinal);

    public IReadOnlyList<SkillDescriptor> Catalog(bool includePrivate, bool enabledOnly)
    {
        IReadOnlySet<string> disabled = repository.DisabledNames();
        IEnumerable<SkillDescriptor> entries = seeds.Values.Select(doc =>
            new SkillDescriptor(doc.Name, doc.Description, doc.Digest, null, true, !disabled.Contains(doc.Name)));
        if (includePrivate)
        {
            entries = entries.Concat(repository.Installed().Select(item => item with { Enabled = !disabled.Contains(item.Name) }));
        }

        return entries.Where(item => !enabledOnly || item.Enabled).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<SkillDescriptor> Search(string query, bool includePrivate, int limit, int skip = 0)
    {
        HashSet<string> words = Words(query);
        HashSet<string> named = ExplicitSkill().Matches(query).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return Catalog(includePrivate, enabledOnly: true)
            .Select(item => (Item: item, Score: Score(item, named, words)))
            .Where(hit => string.IsNullOrWhiteSpace(query) || hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Item.Name, StringComparer.Ordinal)
            .Skip(skip)
            .Take(limit)
            .Select(hit => hit.Item).ToArray();
    }

    public SkillDocument? Open(string name, bool includePrivate, bool enabledOnly) => Select(name, includePrivate, enabledOnly)?.Document;

    public (SkillDocument Document, Guid? RevisionId)? Select(string name, bool includePrivate, bool enabledOnly)
    {
        SkillDocument.ValidateName(name);
        if (seeds.TryGetValue(name, out SkillDocument? bundled))
        {
            return !enabledOnly || !repository.DisabledNames().Contains(name) ? (bundled, null) : null;
        }

        return includePrivate && repository.ReadInstalled(name, enabledOnly) is { } revision
            ? (Verify(revision), revision.Summary.RevisionId) : null;
    }

    public SkillRevisionSummary Propose(string markdown, Guid? conversationId, string rationale, Guid? expectedBase = null)
    {
        SkillDocument document = parser.Parse(markdown);
        if (seeds.ContainsKey(document.Name))
        {
            throw new ArgumentException("Bundled skills are read-only. To adapt one, use a different skill name.");
        }

        return repository.Propose(document, conversationId, rationale, expectedBase);
    }

    public SkillRevision? ReadRevision(Guid id)
    {
        SkillRevision? revision = repository.Read(id);
        if (revision is not null)
        {
            Verify(revision);
        }

        return revision;
    }

    public IReadOnlyList<SkillRevisionSummary> History(string? name, bool pendingOnly, int limit) =>
        repository.History(name, pendingOnly, limit);

    public bool Resolve(Guid id, bool accept)
    {
        if (accept)
        {
            _ = ReadRevision(id) ?? throw new ArgumentException("Unknown skill draft.");
        }

        return repository.Resolve(id, accept);
    }

    public bool SetEnabled(string name, bool enabled)
    {
        SkillDocument.ValidateName(name);
        if (!seeds.ContainsKey(name) && !repository.Installed().Any(item => item.Name == name))
        {
            throw new ArgumentException("Unknown installed skill.");
        }

        return repository.SetEnabled(name, enabled);
    }

    private SkillDocument Verify(SkillRevision revision)
    {
        SkillDocument document = parser.Parse(revision.Markdown);
        return document.Digest == revision.Summary.Digest && document.Name == revision.Summary.Name && document.Description == revision.Summary.Description
            ? document
            : throw new InvalidOperationException("Skill content did not match the reviewed revision.");
    }

    private static int Score(SkillDescriptor item, HashSet<string> named, HashSet<string> words)
    {
        if (named.Contains(item.Name))
        {
            return 10_000;
        }

        HashSet<string> names = Words(item.Name);
        HashSet<string> description = Words(item.Description);
        return words.Sum(word => names.Contains(word) ? 5 : description.Contains(word) ? 1 : 0);
    }

    private static HashSet<string> Words(string text) => Word().Matches(text.ToLowerInvariant())
        .Select(match => match.Value)
        .Where(word => word is not ("about" or "and" or "are" or "can" or "could" or "does" or "for" or "from" or "have" or "help" or "how" or
            "need" or "our" or "please" or "should" or "some" or "than" or "that" or "the" or "their" or "then" or "this" or "use" or
            "using" or "want" or "was" or "what" or "when" or "where" or "which" or "will" or "with" or "would" or "you" or "your"))
        .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[a-z0-9]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<![\w$])\$([a-z0-9-]{1,64})(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitSkill();
}
