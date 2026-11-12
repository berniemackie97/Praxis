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
        return provider.ProviderId switch
        {
            VerificationProvider.Ollama => new OllamaChatModelConnector().Connect(
                new ModelProfile(ModelProfile.LocalEnrichment, ModelProfile.OllamaProvider,
                    provider.Endpoint!, provider.Model!, 32_768, true).Normalized()),
            VerificationProvider.Codex => new ModelConnection(
                new CodexChatModel(provider.Executable!, provider.Model)),
            VerificationProvider.ClaudeCode => new ModelConnection(
                new ClaudeCodeChatModel(provider.Executable!, provider.Model ?? "sonnet")),
            _ => throw new NotSupportedException("The verification provider is not supported.")
        };
    }

    public IReadOnlyDictionary<string, string> DiscoverExecutables()
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        IEnumerable<string> directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified);
        foreach ((string provider, string command) in new[] {
            (VerificationProvider.Codex, "codex"), (VerificationProvider.ClaudeCode, "claude") })
        {
            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, command);
                if (File.Exists(candidate))
                {
                    found[provider] = candidate;
                    break;
                }
            }
        }

        return found;
    }
}
