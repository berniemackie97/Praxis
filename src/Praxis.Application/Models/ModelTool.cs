namespace Praxis.Application.Models;

/// <summary>
/// A model tool found on this Mac (Claude Code, Codex or Ollama): where it is, its version, whether
/// it is signed in, the models it offers, and what is wrong when it cannot be used.
/// </summary>
public sealed record ModelTool(
    string Id, string? Path, string? Version, bool SignedIn, string? Account, string? Problem, IReadOnlyList<ModelChoice> Models)
{
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";
    public const string Ollama = "ollama";
}
