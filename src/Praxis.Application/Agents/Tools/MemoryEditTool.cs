using System.Text.Json;
using Praxis.Application.Agents.Memory;
using Praxis.Domain.Agents;
using Praxis.Domain.Verification;

namespace Praxis.Application.Agents.Tools;

/// <summary>Local note maintenance with an explicit owner request and a checked source revision.</summary>
public sealed class MemoryEditTool(IChatMemoryNotes notes, IChatMemoryStore store, IAgentConversationRepository legacy) : IAgentTool
{
    public const string ToolName = "memory_edit";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Maintain private memory. Use find to discover existing Inbox Markdown and its digest; edit replaces one exact passage (empty replacement removes it), or appends when previous is omitted. name sets your own display name when asked. fact updates or forgets a saved memory ID. Write only on explicit owner instruction.",
        ToolArguments.Schema("""
            {"type":"object","required":["action"],"properties":{
              "action":{"enum":["find","edit","name","fact"]},"title":{"type":"string"},"sourceId":{"type":"string"},
              "digest":{"type":"string"},"previous":{"type":"string"},"replacement":{"type":"string"},
              "name":{"type":"string"},"memoryId":{"type":"string"},"forget":{"type":"boolean"}
            }}
            """), ToolTiers.Write);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.BackendIsLocal || context.BackendId != VerificationProvider.Ollama)
        {
            return AgentToolOutput.Error("permission_denied", "Private memory edits require managed local Ollama.");
        }

        string owner = context.OwnerText ?? "";
        ToolArguments args = new(arguments);
        switch (args.Choice("action", ["find", "edit", "name", "fact"], ""))
        {
            case "find":
                string title = args.RequireText("title", 120);
                if (!MemoryText.ExplicitEdit(owner) || !MemoryText.Mentions(owner, title))
                {
                    return ExplicitRequestRequired();
                }
                return AgentToolOutput.Of(new
                {
                    notes = notes.Find(title, cancellationToken)
                    .Where(note => MemoryText.Mentions(owner, note.Title))
                    .Select(note => new { note.SourceId, note.Title, note.Digest, Markdown = note.Markdown[..Math.Min(12_000, note.Markdown.Length)] })
                    .Take(3)
                });
            case "edit":
                if (!MemoryText.ExplicitEdit(owner))
                {
                    return ExplicitRequestRequired();
                }
                ChatMemoryNote current = notes.Open(args.RequireGuid("sourceId"), cancellationToken);
                if (!MemoryText.Mentions(owner, current.Title))
                {
                    return ExplicitRequestRequired();
                }
                if (args.RequireText("digest", 64) != current.Digest)
                {
                    throw new InvalidOperationException("The note changed. Find it again before editing.");
                }
                string? old = args.Text("previous", 8_000);
                string replacement = args.Text("replacement", 8_000) ?? "";
                if (replacement.Length > 0 && replacement.Split('\n').Any(line =>
                    line.Trim().Length != 0 && !MemoryText.SafeLine(line.Trim(), 8_000)))
                {
                    throw new ArgumentException("Private notes cannot receive credentials or unsafe markup through this tool.");
                }
                string newline = current.Markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                string markdown = old is null
                    ? Append(current.Markdown, replacement, newline)
                    : ReplaceOnce(current.Markdown, old.ReplaceLineEndings(newline), replacement.ReplaceLineEndings(newline));
                bool updated = await notes.ReplaceAsync(current, markdown, cancellationToken);
                return AgentToolOutput.Of(new { current.Title, updated });
            case "name":
                string name = args.RequireText("name", 48).Trim();
                if (!MemoryText.MayNameSelf(owner) || !MemoryText.ValidName(name) || !MemoryText.NameMatchesRequest(owner, name))
                {
                    return ExplicitRequestRequired();
                }
                store.SaveName(name);
                return AgentToolOutput.Of(new { name });
            case "fact":
                if (!MemoryText.ExplicitEdit(owner) || !(MemoryText.HasPhrase(owner, "memory") || MemoryText.HasPhrase(owner, "preference")))
                {
                    return ExplicitRequestRequired();
                }
                Guid id = args.RequireGuid("memoryId");
                bool forget = args.Boolean("forget", false);
                string? value = forget ? null : args.RequireText("replacement", 300).Trim();
                if (value is not null && !MemoryText.SafeLine(value, 300))
                {
                    throw new ArgumentException("The memory contains unsafe or secret text.");
                }
                return AgentToolOutput.Of(new { updated = legacy.EditMemory(id, value) });
            default:
                throw new ArgumentException("A memory action is required.");
        }
    }

    private static string Append(string markdown, string addition, string newline)
    {
        if (addition.Length == 0)
        {
            throw new ArgumentException("Provide text to append or a passage to remove.");
        }
        return MemoryText.Key(markdown).Contains(MemoryText.Key(addition), StringComparison.Ordinal)
            ? markdown : markdown.TrimEnd() + newline + newline + addition.ReplaceLineEndings(newline) + newline;
    }

    private static string ReplaceOnce(string markdown, string old, string replacement)
    {
        int start = markdown.IndexOf(old, StringComparison.Ordinal);
        if (start < 0 || markdown.IndexOf(old, start + 1, StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("The passage must match one location exactly. Find the note again.");
        }
        return markdown[..start] + replacement + markdown[(start + old.Length)..];
    }

    private static AgentToolOutput ExplicitRequestRequired() =>
        AgentToolOutput.Error("permission_denied", "The latest owner request must explicitly authorize this private memory change.");
}
