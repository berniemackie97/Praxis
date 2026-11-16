using System.Text.RegularExpressions;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Memory;

/// <summary>Accepts only direct owner requests with two literal names. Source text and model output never enter this parser.</summary>
internal static partial class OwnerNameCorrection
{
    internal static AgentNameCorrection? Parse(string text)
    {
        if (text.Length > 480)
        {
            return null;
        }

        Match explicitSyntax = Command().Match(text);
        Match statement = explicitSyntax.Success ? explicitSyntax : Statement().Match(text);
        if (!statement.Success)
        {
            return null;
        }

        string before = statement.Groups["before"].Value.Trim();
        string statedAlias = statement.Groups["alias"].Value;
        string after = statement.Groups["after"].Value.Trim().Trim('"', '“', '”').TrimEnd('.');
        if ((statedAlias.Length != 0 && !before.Equals(statedAlias, StringComparison.OrdinalIgnoreCase)) ||
            !SafeName(before) || !SafeName(after) || before.Equals(after, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new AgentNameCorrection(before, after, statement.Groups["business"].Success);
    }

    internal static string? Answer(string text, IReadOnlyList<AgentNameCorrection> corrections)
    {
        if (BusinessQuestion().IsMatch(text))
        {
            string[] names = corrections.Where(item => item.Business).Select(item => item.Current)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return names.Length == 1 ? $"Your saved business name is **{names[0]}**." : null;
        }

        Match question = NameQuestion().Match(text);
        if (!question.Success)
        {
            return null;
        }

        string label = question.Groups["label"].Value.Trim().Trim('"', '“', '”');
        AgentNameCorrection? correction = corrections.FirstOrDefault(item =>
            item.Previous.Equals(label, StringComparison.OrdinalIgnoreCase) ||
            item.Current.Equals(label, StringComparison.OrdinalIgnoreCase));
        return correction is null ? null : $"The current name is **{correction.Current}**.";
    }

    internal static bool Relevant(AgentNameCorrection correction, string text) =>
        MemoryText.HasPhrase(text, correction.Previous) || MemoryText.HasPhrase(text, correction.Current) ||
        correction.Business && BusinessQuestion().IsMatch(text);

    private static bool SafeName(string value) => MemoryText.SafeLine(value, 120) && value.Any(char.IsLetter) &&
        NameCharacters().IsMatch(value) && !InstructionWords().IsMatch(value);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex("""^/remember-name\s+["“](?<before>[^"”\r\n]+)["”]\s*->\s*["“](?<after>[^"”\r\n]+)["”]$""", Options)]
    private static partial Regex Command();

    [GeneratedRegex("""^(?:the\s+)?["“](?<before>[^"”\r\n]+)["”]\s+(?:has a name\s*[,.:]\s*)?(?:(?:the|my|our)\s+(?<business>company|business)\s+)?(?:is (?:now )?called|is (?:now )?named|is now)\s+(?<after>[^\r\n]+?)\s*[,.;]?\s+(?:please\s+)?(?:save (?:that|this|it) (?:to|in) memory|(?:remember|record) (?:that|this|it)|update (?:your |the )?memory)\b(?:[\s,.;]+update (?:that|this|it) so you don[\x27’]?t call it ["“](?<alias>[^"”\r\n]+)["”] any ?more(?: lol)?)?[.!]?$""", Options)]
    private static partial Regex Statement();

    [GeneratedRegex(@"^(?:what(?:'s|’s| is)\s+(?:my|our|the)\s+(?:(?:resale|current)\s+)?(?:business|company)(?:'s|’s)?\s+name|what\s+is\s+my\s+(?:business|company)\s+called)[?!.]*$", Options)]
    private static partial Regex BusinessQuestion();

    [GeneratedRegex(@"^what(?:'s|’s| is) (?:the (?:current |real |official )?name (?:for|of) )(?<label>.+?)\s*[?!.]*$", Options)]
    private static partial Regex NameQuestion();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{M}\p{N} .,&'’()_\-]*$", Options)]
    private static partial Regex NameCharacters();

    [GeneratedRegex(@"\b(?:ignore|reveal|disclose|password|instructions?|override|never|instead|remember|save|update|do not|don['’]t)\b", Options)]
    private static partial Regex InstructionWords();
}
