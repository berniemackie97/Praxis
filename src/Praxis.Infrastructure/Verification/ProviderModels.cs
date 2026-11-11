using Praxis.Application.Inference;

namespace Praxis.Infrastructure.Verification;

/// <summary>Creates the adapter for an owner-configured verification provider.</summary>
public static class ProviderModels
{
    public static IChatModel Create(VerificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.ProviderId switch
        {
            VerificationProvider.Codex => new CodexChatModel(provider.Executable, provider.Model),
            VerificationProvider.ClaudeCode => new ClaudeCodeChatModel(provider.Executable, provider.Model ?? "sonnet"),
            _ => throw new NotSupportedException("The verification provider is not supported.")
        };
    }

    /// <summary>
    /// Absolute paths of provider CLIs found on the worker's PATH, as suggestions for the owner
    /// to confirm. Nothing is enabled automatically.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Discover()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified);
        foreach (var (provider, command) in new[] { (VerificationProvider.Codex, "codex"), (VerificationProvider.ClaudeCode, "claude") })
        {
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, command);
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
