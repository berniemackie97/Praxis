namespace Praxis.Domain.Verification;

/// <summary>A stronger model the owner has enabled, ordered for routing by cost weight.</summary>
public sealed record VerificationProvider(
    string ProviderId, string Executable, string? Model, int CostWeight, int DailyLimit, bool Enabled)
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";

    /// <summary>Rejects unsupported providers and out-of-range routing weights or daily limits.</summary>
    public void EnsureValid()
    {
        if (ProviderId is not (Codex or ClaudeCode))
        {
            throw new ArgumentException("The verification provider is not supported.", nameof(ProviderId));
        }

        if (CostWeight is < 0 or > 1000 || DailyLimit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(CostWeight), "Weights are 0–1000 and daily limits 1–500.");
        }
    }
}
