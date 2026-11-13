namespace Praxis.Domain.Agents;

/// <summary>How a backend is reached: a tool-calling HTTP API Praxis drives, or a CLI agent that drives Praxis tools over MCP.</summary>
public static class AgentBackendKinds
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string Anthropic = "anthropic";
    public const string ClaudeCode = "claude_code";
    public const string Codex = "codex";

    public static readonly IReadOnlyList<string> All = [OpenAiCompatible, Anthropic, ClaudeCode, Codex];

    /// <summary>CLI agents run their own loop and reach Praxis tools through the MCP relay.</summary>
    public static bool IsSession(string kind) => kind is ClaudeCode or Codex;
}
