using Praxis.Application.Inference;
using Praxis.Application.Verification;
using Praxis.Domain.Verification;

namespace Praxis.Infrastructure.Inference.Subscription;

/// <summary>Creates subscription CLI adapters (Codex, Claude Code) for owner-configured verification providers.</summary>
public sealed class SubscriptionVerifierModelFactory : IVerifierModelFactory
{
    public IChatModel Create(VerificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.ProviderId switch
        {
            VerificationProvider.Codex => new CodexChatModel(provider.Executable, provider.Model),
            VerificationProvider.ClaudeCode => new ClaudeCodeChatModel(provider.Executable, provider.Model ?? "sonnet"),
            _ => throw new NotSupportedException("The verification provider is not supported.")
        };
    }

    public IReadOnlyDictionary<string, string> DiscoverExecutables()
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        IEnumerable<string> directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified);
        foreach ((string provider, string command) in new[] { (VerificationProvider.Codex, "codex"), (VerificationProvider.ClaudeCode, "claude") })
        {
            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, command);
                if (File.Exists(candidate))
                {
                    // Keep the PATH entry rather than its link target: package managers replace
                    // versioned targets on update, while the PATH link stays stable.
                    found[provider] = candidate;
                    break;
                }
            }
        }

        return found;
    }
}
