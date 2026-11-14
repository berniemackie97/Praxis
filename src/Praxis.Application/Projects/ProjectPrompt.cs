using System.Text.Json;
using Praxis.Application.Inference;

namespace Praxis.Application.Projects;

/// <summary>The one structured request that turns a repository map into a project understanding.</summary>
public static class ProjectPrompt
{
    public const int OutputTokens = 2_048;
    public const int ContextTokens = 32_768;

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "purpose":{"type":"string"},
          "summary":{"type":"string"},
          "components":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"path":{"type":"string"},"role":{"type":"string"}},"required":["name","path","role"]}},
          "entryPoints":{"type":"array","items":{"type":"string"}},
          "buildCommands":{"type":"array","items":{"type":"string"}}},
         "required":["purpose","summary","components","entryPoints","buildCommands"]}
        """).RootElement.Clone();

    public static ChatRequest Create(RepositoryMap map) => new(
    [
        new ChatMessage(ChatRole.System, """
            You describe a software project for its owner from a map of its files. Be concrete and brief.
            purpose: one sentence on what it is for. summary: at most 6 sentences on how it is built (architecture, main parts, data flow).
            components: up to 12 main parts, each with the exact folder or file path from the file list and its role.
            entryPoints: exact file paths where execution starts (main programs, app entry, server startup).
            buildCommands: only commands listed under "Declared commands" or written in the README.
            Use only paths that appear in the file list. Do not invent dependencies, versions or commands.
            """),
        new ChatMessage(ChatRole.User, map.Text)
    ], Schema, OutputTokens, ContextTokens);
}
