namespace Praxis.Application.Policy;

/// <summary>Small owner settings that tune how Praxis runs on this machine.</summary>
public interface ISettingsStore
{
    string? Read(string key);

    void Write(string key, string value);
}
