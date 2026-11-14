using Praxis.Application.Inference;
using Praxis.Application.Verification;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Inference;

/// <summary>Explicit local Ollama and installed subscription-CLI verifier sessions.</summary>
public sealed class VerificationModelFactory : IVerifierModelFactory
{
    public ModelConnection Create(VerificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.EnsureValid();
        try
        {
            return provider.ProviderId switch
            {
                VerificationProvider.Ollama => new OllamaChatModelConnector().Connect(
                    new ModelProfile(ModelProfile.LocalEnrichment, ModelProfile.OllamaProvider,
                        provider.Endpoint!, provider.Model!, 32_768, true).Normalized()),
                VerificationProvider.Codex => new ModelConnection(
                    new CodexChatModel(provider.Executable!, provider.Model, effort: provider.Effort)),
                VerificationProvider.ClaudeCode => new ModelConnection(
                    new ClaudeCodeChatModel(provider.Executable!, provider.Model, effort: provider.Effort)),
                _ => throw new NotSupportedException("The verification provider is not supported.")
            };
        }
        catch (ArgumentException exception)
        {
            throw new InferenceException(InferenceFailure.Unavailable,
                "The configured verifier is no longer available.", exception);
        }
    }

    public IReadOnlyDictionary<string, string> DiscoverExecutables()
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        // The worker's PATH is minimal when the app starts it; look where installers put the CLIs too.
        foreach ((string provider, string command) in new[] {
            (VerificationProvider.Codex, "codex"), (VerificationProvider.ClaudeCode, "claude") })
        {
            if (ModelToolDetector.DefaultCandidates(command).FirstOrDefault(File.Exists) is { } candidate)
            {
                found[provider] = candidate;
            }
        }

        return found;
    }
}
