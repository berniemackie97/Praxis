namespace Praxis.Domain.Verification;

/// <summary>A stronger model the owner has enabled, ordered for routing by cost weight.</summary>
public sealed record VerificationProvider(
    string ProviderId, string Executable, string? Model, int CostWeight, int DailyLimit, bool Enabled)
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
}
