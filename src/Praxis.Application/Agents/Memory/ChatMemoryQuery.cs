using System.Text.RegularExpressions;

namespace Praxis.Application.Agents.Memory;

/// <summary>Conservative lexical keys for small conversation summaries.</summary>
public static partial class ChatMemoryQuery
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "before", "brain", "chat", "chats", "conversation", "conversations", "could",
        "decide", "decided", "discuss", "discussed", "does", "dream", "earlier", "from", "give", "have", "hello", "help",
        "into", "know", "last", "learn", "learned", "memory", "more", "need", "notes", "only", "other", "please", "previous",
        "recall", "remember", "said", "should", "some", "summarize", "tell", "than", "thank", "thanks", "that", "their",
        "them", "then", "there", "these", "they", "think", "this", "those", "through", "want", "were", "what", "when",
        "where", "which", "with", "would", "your", "yours"
    };

    public static string Build(string text) => string.Join(' ', Words().Matches(text).Select(match => match.Value)
        .Where(term => !Common.Contains(term)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6));

    [GeneratedRegex(@"[\p{L}\p{N}_]{3,32}")]
    private static partial Regex Words();
}
