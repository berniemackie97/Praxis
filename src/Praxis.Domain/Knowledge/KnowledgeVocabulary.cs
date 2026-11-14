using System.Text;

namespace Praxis.Domain.Knowledge;

/// <summary>Closed vocabularies and name normalization shared by proposals and the entity layer.</summary>
public static class KnowledgeVocabulary
{
    public const int MaximumNameLength = 120;

    public static readonly IReadOnlySet<string> EntityTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "person", "organization", "project", "technology", "concept", "place", "document", "event", "other", "class", "function", "binary_module", "data_asset"
    };

    public const string TechnicalTypeGuide = """
        A class or struct is class; a callable routine or method is function; a compiled executable,
        shared library or DLL is binary_module; a named runtime data or resource file is data_asset.
        A source report or article is document. Project is a whole product, game, repository or initiative.
        Do not label code symbols as people, organizations, projects, or technologies.
        """;

    /// <summary>Directional predicates: "subject predicate object", e.g. C3Reader parses PHY.</summary>
    public static readonly IReadOnlySet<string> Predicates = new HashSet<string>(StringComparer.Ordinal)
    {
        "uses", "part_of", "contains", "depends_on", "implements", "parses", "produces",
        "created_by", "owned_by", "works_on", "member_of", "located_in", "pays", "references", "related_to", "calls", "inherits_from"
    };

    private static readonly char[] Enclosing = ['"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>', '.', ',', ':', ';', '!', '?', '“', '”', '‘', '’'];

    /// <summary>
    /// Identity key for a name: Unicode compatibility-normalized, whitespace collapsed, enclosing
    /// punctuation, a leading "the" and a trailing possessive removed, then upper-cased. Distinct
    /// spellings stay distinct (no fuzzy matching); merging them is an owner decision.
    /// </summary>
    public static string? NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string collapsed = string.Join(' ', name.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(Enclosing).Trim();
        if (collapsed.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            collapsed = collapsed[4..];
        }

        foreach (string possessive in new[] { "'s", "’s" })
        {
            if (collapsed.EndsWith(possessive, StringComparison.OrdinalIgnoreCase))
            {
                collapsed = collapsed[..^possessive.Length];
            }
        }

        string key = collapsed.Trim().ToUpperInvariant();
        return key.Length is >= 1 and <= MaximumNameLength && key.Any(char.IsLetterOrDigit) ? key : null;
    }
}
