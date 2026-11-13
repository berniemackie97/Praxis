using System.Collections.Concurrent;

namespace Praxis.Application.Agents;

/// <summary>
/// API keys the owner's app hands to the worker over the private pipe, held in memory for the
/// worker's lifetime only — never written to the catalog, disk, logs or a command line. Accounts
/// the app has not provided fall back to <paramref name="fallback"/> (keys provisioned in the
/// Keychain for the system <c>security</c> tool).
/// </summary>
public sealed class SessionSecretStore(ISecretStore fallback) : ISecretStore
{
    private const int MaximumAccounts = 64;
    private readonly ConcurrentDictionary<string, string> secrets = new(StringComparer.Ordinal);

    public string? Read(string account) =>
        secrets.TryGetValue(account, out string? secret) ? secret : fallback.Read(account);

    public void Set(string account, string secret)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 128 || string.IsNullOrWhiteSpace(secret) || secret.Length > 4_096)
        {
            throw new ArgumentException("The key or its account name is invalid.");
        }

        if (!secrets.ContainsKey(account) && secrets.Count >= MaximumAccounts)
        {
            throw new InvalidOperationException("Too many API keys are loaded.");
        }

        secrets[account] = secret;
    }

    public bool Remove(string account) => secrets.TryRemove(account, out _);
}
