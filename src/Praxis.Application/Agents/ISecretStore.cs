namespace Praxis.Application.Agents;

/// <summary>Reads owner-stored API keys by account name (the macOS Keychain in production). Keys never enter the catalog or logs.</summary>
public interface ISecretStore
{
    string? Read(string account);
}
