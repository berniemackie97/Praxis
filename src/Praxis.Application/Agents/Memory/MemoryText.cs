using System.Text;
using System.Text.RegularExpressions;

namespace Praxis.Application.Agents.Memory;

internal static partial class MemoryText
{
    internal static string Key(string text) =>
        string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    internal static bool Mentions(string statement, string title)
    {
        string[] words = Words().Matches(title).Select(word => word.Value).ToArray();
        return words.Length != 0 && (HasPhrase(statement, title) ||
            words[0].Length >= 4 && !Generic.Contains(words[0]) && HasPhrase(statement, words[0]));
    }

    internal static bool HasPhrase(string statement, string phrase) =>
        Regex.IsMatch(statement, @"(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static bool ExplicitEdit(string text) => Edit().IsMatch(text) && !Negation().IsMatch(text);
    internal static string? ConfirmedKind(string text)
    {
        if (Negation().IsMatch(text) || text.Contains('?'))
        {
            return null;
        }

        Match match = Confirmed().Match(text);
        return match.Success ? match.Groups["decision"].Success ? "Decision" : match.Groups["problem"].Success ? "Problem" : "Fix" : null;
    }

    internal static string ClaimKey(string text)
    {
        string claim = text.Trim().TrimStart('-', '*').Trim();
        claim = EntryLabel().Replace(claim, "");
        return Key(Confirmed().Replace(claim, ""));
    }
    internal static bool SafeLine(string text, int maximum) => text.Length is > 0 && text.Length <= maximum &&
        !text.Any(char.IsControl) && !Secrets().IsMatch(text) && !text.Contains('<') && !text.Contains("](", StringComparison.Ordinal);
    internal static bool MayNameSelf(string text) => Naming().IsMatch(text) && !Negation().IsMatch(text);

    internal static bool ValidName(string name) => name.Length is > 0 and <= 48 &&
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3 &&
        name.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '\'') &&
        name.Any(char.IsLetter);

    internal static bool ChooseNameRequest(string text) => ChooseOnly().IsMatch(text.Trim());

    internal static bool NameMatchesRequest(string owner, string name)
    {
        Match direct = DirectName().Match(owner.Trim());
        return direct.Success ? direct.Groups["name"].Value.Trim(' ', '!', '.').Equals(name, StringComparison.OrdinalIgnoreCase)
            : ChooseName().IsMatch(owner);
    }

    private static readonly HashSet<string> Generic = new(["about", "project", "owner", "notes", "personal", "general", "meeting", "business", "workshop"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
    [GeneratedRegex(@"\b(?:update|change|save|replace|remove|delete|correct|rename|remember|record|add|forget)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Edit();
    [GeneratedRegex(@"^(?:(?<decision>(?:we|i) (?:decided|agreed)(?: to| that)?|(?:final )?decision\s*:)|(?<problem>(?:the )?(?:problem|issue|bug) (?:is|with)|(?:problem|issue|bug)\s*:)|(?<fix>(?:we|i) (?:fixed|resolved)|(?:fix|resolution)\s*:))\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Confirmed();
    [GeneratedRegex(@"^(?:Decision|Problem|Fix):\*\*\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntryLabel();
    [GeneratedRegex(@"\b(?:don['’]t|do not|never|hypothetical|pretend|for example|ignore previous)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Negation();
    [GeneratedRegex(@"\b(?:password|passphrase|recovery code|api key|secret key|access token|private key|social security)\b|(?:\d[ -]?){9,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Secrets();
    [GeneratedRegex(@"(?:^|[.!?]\s+)(?:please\s+)?(?:choose|pick|decide on) (?:your (?:own )?name|a name for yourself)\b|(?:^|[.!?]\s+)(?:please\s+)?(?:name|rename|call) yourself\b|^/name\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Naming();
    [GeneratedRegex(@"^(?:/name\s+|(?:please\s+)?(?:name|rename|call) yourself\s+)(?<name>[^\r\n]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectName();
    [GeneratedRegex(@"(?:^|[.!?]\s+)(?:please\s+)?(?:choose|pick|decide on) (?:your (?:own )?name|a name for yourself)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChooseName();
    [GeneratedRegex(@"^(?:please\s+)?(?:choose|pick|decide on) (?:your (?:own )?name|a name for yourself)[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChooseOnly();
}
