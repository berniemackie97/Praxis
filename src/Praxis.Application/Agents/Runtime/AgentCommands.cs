using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

public static class AgentCommands
{
    public static IReadOnlyList<AgentCommandDefinition> All { get; } =
    [
        new("correct", "Correct Brain knowledge", "Save an owner correction and map supported entity or relationship repairs.", "State the correction and relationship", ToolTiers.Propose, true, false),
        new("dream", "Dream", "Review Brain evidence and queue supported repairs for approval.", "Focus or instructions (optional)", ToolTiers.Propose, true, false),
        new("name", "Name Praxis", "Set Praxis's display name, or let a local model choose one.", "Display name (optional)", ToolTiers.Write, false, true),
        new("remember-name", "Correct a name", "Record an exact old and current name without merging unrelated entities.", "\"Old name\" -> \"Current name\"", ToolTiers.Propose, true, true)
    ];

    public static AgentCommandRequest? Parse(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '/')
        {
            return null;
        }
        int end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }
        string name = trimmed[1..end];
        if (name.Length > 32 || !char.IsAsciiLetter(name[0]) || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return null;
        }
        return new(name.ToLowerInvariant(), trimmed[end..].Trim());
    }
}
