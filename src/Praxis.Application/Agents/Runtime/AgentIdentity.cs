using System.Text.RegularExpressions;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Praxis owns its assistant identity. The provider remains visible separately and is never guessed.</summary>
internal static partial class AgentIdentity
{
    public static string? Answer(string request, string name, AgentBackendProfile profile)
    {
        string normalized = request.Trim().TrimEnd('?', '!', '.').Replace('’', '\'');
        if (normalized.Length > 100)
        {
            return null;
        }

        if (AssistantQuestion().IsMatch(normalized))
        {
            return $"I’m {name}, your private Second Brain assistant. I help you find and use what you’ve saved.";
        }

        if (ModelQuestion().IsMatch(normalized))
        {
            return profile.Model == "default"
                ? $"This chat uses the {profile.BackendId} connection with its default model. Praxis is the assistant you’re talking to."
                : $"This chat uses `{profile.Model}` through the {profile.BackendId} connection. Praxis is the assistant you’re talking to.";
        }

        return null;
    }

    [GeneratedRegex(@"^(?:who are you|what are you|what(?:'s| is) your name|who (?:made|created|built) you|introduce yourself|are you (?:gemma|gemini|google|claude|chatgpt|codex|praxis))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssistantQuestion();

    [GeneratedRegex(@"^(?:what model are you(?: using)?|which model (?:are you using|is this)|what(?:'s| is) the (?:underlying|active|selected) model)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelQuestion();
}
