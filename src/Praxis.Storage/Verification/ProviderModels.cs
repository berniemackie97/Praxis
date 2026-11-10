using Praxis.Inference;

namespace Praxis.Storage.Verification;

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
                    found[provider] = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                    break;
                }
            }
        }

        return found;
    }
}
