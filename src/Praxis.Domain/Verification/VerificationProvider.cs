using Praxis.Domain.Models;

namespace Praxis.Domain.Verification;

/// <summary>
/// Owner-configured model provider for intake and verification. CLI executables and loopback model
/// endpoints are distinct. A daily limit of <see cref="Unlimited"/> means no cap.
/// </summary>
public sealed record VerificationProvider(
    string ProviderId, string? Executable, string? Model, int CostWeight, int DailyLimit, bool Enabled,
    string? Endpoint = null, int Revision = 1, string? Effort = null)
{
    public const int Unlimited = 0;
    public const int MaximumDailyLimit = 100_000;

    public const string Ollama = "ollama";
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";

    public bool IsLocal => ProviderId == Ollama;

    /// <summary>True while <paramref name="startedToday"/> attempts leave room under the daily limit.</summary>
    public bool WithinLimit(int startedToday) => DailyLimit == Unlimited || startedToday < DailyLimit;

    public void EnsureValid()
    {
        if (ProviderId is not (Ollama or Codex or ClaudeCode))
        {
            throw new ArgumentException("The verification provider is not supported.", nameof(ProviderId));
        }

        if (Revision < 1 || CostWeight is < 0 or > 1000 || DailyLimit is < Unlimited or > MaximumDailyLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(CostWeight), "Weights are 0–1000 and daily limits 0 (unlimited) to 100,000.");
        }

        if (!ModelEffort.IsValid(Effort))
        {
            throw new ArgumentException("The reasoning effort is invalid.", nameof(Effort));
        }

        if (IsLocal)
        {
            if (Executable is not null || Endpoint is null || Model is null)
            {
                throw new ArgumentException("Local verification requires an Ollama endpoint and model, not a CLI executable.");
            }

            _ = new ModelProfile(ModelProfile.LocalEnrichment, ModelProfile.OllamaProvider,
                Endpoint, Model, 32_768, true).Normalized();
        }
        else if (Endpoint is not null || string.IsNullOrWhiteSpace(Executable))
        {
            throw new ArgumentException("External verification requires an executable and no Ollama endpoint.");
        }
    }
}
