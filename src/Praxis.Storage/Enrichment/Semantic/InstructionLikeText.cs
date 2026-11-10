using System.Text.RegularExpressions;

namespace Praxis.Storage.Enrichment.Semantic;

/// <summary>
/// Flags evidence whose sentence reads like an instruction to an AI model ("ignore previous
/// instructions", "output an entity named ..."). Such text can steer extraction toward names it
/// contains, so a grounded quote does not make the result trustworthy. A flag withholds the
/// proposal from the knowledge graph until the owner accepts it or a verifier confirms it; it
/// never deletes or rejects anything by itself.
/// </summary>
internal static partial class InstructionLikeText
{
    internal const string Flag = "instruction_like";

    [GeneratedRegex("""
        \b(ignore|disregard|forget|override)\b.{0,40}\b(instructions?|prompts?|rules?|above|previous|prior)\b
        | \b(you\s+are|you\s+must|you\s+should|act\s+as|pretend\s+to\s+be)\b.{0,40}\b(ai|assistant|model|chatbot|system|extractor)\b
        | \b(output|return|respond|reply|print|list|report|add|include|emit)\b.{0,60}\b(entit(y|ies)|json|topics?|organi[sz]ations?|answer|response|schema)\b
        | \b(system\s+prompt|jailbreak|as\s+an\s+ai|language\s+model)\b
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Instruction();

    /// <summary>Checks the sentence(s) of <paramref name="text"/> that contain the evidence span.</summary>
    internal static bool Matches(string text, int offset, int length)
    {
        var start = text.LastIndexOfAny(['.', '!', '?', '\n'], Math.Max(0, offset - 1)) + 1;
        var endIndex = text.IndexOfAny(['.', '!', '?', '\n'], Math.Min(text.Length - 1, offset + length));
        var end = endIndex < 0 ? text.Length : endIndex + 1;
        try
        {
            return Instruction().IsMatch(text.AsSpan(start, end - start));
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // Unusually complex text is treated as suspicious, not as clean.
        }
    }
}
