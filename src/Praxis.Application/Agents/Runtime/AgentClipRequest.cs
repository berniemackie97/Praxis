using System.Text.RegularExpressions;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Recognizes direct requests to retrieve an existing timed clip, including with small or tool-free models.</summary>
internal static partial class AgentClipRequest
{
    public static string? Query(string text)
    {
        if (text.Length > 800)
        {
            return null;
        }

        Match match = ClipIntent().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        string focus = HowItWorks().Replace(match.Groups["topic"].Value.Trim(' ', '?', '.', '!'), "${topic}");
        string query = string.Join(' ', Terms().Matches(focus).Take(12).Select(term => term.Value));
        return query.Length == 0 ? null : query;
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:can|could|would|will) you\s+)?(?:(?:give|send|get|find|show|pull up|play|look up)\s+(?:me\s+)?|(?:do you have|is there)\s+)(?:(?:a|an|the|any|my)\s+)?(?:relevant\s+)?(?:video clips?|clips?|videos?|tutorials?|recordings?|segments?|parts?|moments?)\s+(?:on|about|of|for|from|where|that (?:explains?|shows?|covers?))\s+(?<topic>[^\r\n]+)$", Options)]
    private static partial Regex ClipIntent();

    [GeneratedRegex(@"[\p{L}\p{N}]{1,20}", Options)]
    private static partial Regex Terms();

    [GeneratedRegex(@"^how\s+(?<topic>.+?)\s+works?$", Options)]
    private static partial Regex HowItWorks();
}
