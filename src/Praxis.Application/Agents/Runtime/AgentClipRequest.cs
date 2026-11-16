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

    /// <summary>
    /// True when the owner asks to replace or improve clips already shown ("those clips are bad, any others?", "make the clips
    /// better"). <paramref name="sameRecording"/> is set when they keep the same recording ("same video is fine").
    /// </summary>
    public static bool IsRefinement(string text, out bool sameRecording)
    {
        sameRecording = false;
        string trimmed = text.Trim();
        if (trimmed.Length > 240 || Query(trimmed) is not null || !RefinementNoun().IsMatch(trimmed) || !RefinementAsk().IsMatch(trimmed) ||
            NewTopic().IsMatch(trimmed))
        {
            return false;
        }

        sameRecording = SameRecording().IsMatch(trimmed);
        return true;
    }

    /// <summary>The topic of the most recent clip request among <paramref name="earlierUserTexts"/> (newest first).</summary>
    public static string? PreviousQuery(IEnumerable<string> earlierUserTexts) =>
        earlierUserTexts.Select(Query).FirstOrDefault(query => query is not null);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:can|could|would|will) you\s+)?(?:(?:give|send|get|find|show|pull up|play|look up)\s+(?:me\s+)?|(?:do you have|is there)\s+)(?:(?:a|an|the|any|my)\s+)?(?:relevant\s+)?(?:video clips?|clips?|videos?|tutorials?|recordings?|segments?|parts?|moments?)\s+(?:on|about|of|for|from|where|that (?:explains?|shows?|covers?))\s+(?<topic>[^\r\n]+)$", Options)]
    private static partial Regex ClipIntent();

    [GeneratedRegex(@"\b(?:clips?|ones?|moments?|segments?|parts?|videos?|recordings?)\b", Options)]
    private static partial Regex RefinementNoun();

    [GeneratedRegex(@"\b(?:other|different|another|new|more|better|longer|bad|crap|crappy|wrong|useless|short|cut off|improve|redo)\b", Options)]
    private static partial Regex RefinementAsk();

    // "a better video on python" names a new topic; leave it to the model rather than reusing the earlier one.
    [GeneratedRegex(@"\b(?:on|about|regarding|covering|explaining)\s+(?!it\b|that\b|this\b|them\b)[\p{L}\p{N}]", Options)]
    private static partial Regex NewTopic();

    [GeneratedRegex(@"\b(?:same|this|that)\s+(?:video|recording|one|file)\b", Options)]
    private static partial Regex SameRecording();

    [GeneratedRegex(@"[\p{L}\p{N}]{1,20}", Options)]
    private static partial Regex Terms();

    [GeneratedRegex(@"^how\s+(?<topic>.+?)\s+works?$", Options)]
    private static partial Regex HowItWorks();
}
