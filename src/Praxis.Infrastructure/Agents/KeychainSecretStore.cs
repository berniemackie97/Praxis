using System.Diagnostics;
using Praxis.Application.Agents;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Reads generic passwords stored under service "Praxis" in the owner's login Keychain using the
/// system <c>security</c> tool (no shell, bounded wait). The key is held only for the request.
/// </summary>
public sealed class KeychainSecretStore : ISecretStore
{
    private const string Service = "Praxis";

    public string? Read(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 128 || !OperatingSystem.IsMacOS())
        {
            return null;
        }

        ProcessStartInfo start = new("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "find-generic-password", "-s", Service, "-a", account, "-w" })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The Keychain is unavailable.");
        string secret = process.StandardOutput.ReadToEnd().Trim();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        return process.ExitCode == 0 && secret.Length is > 0 and <= 4_096 ? secret : null;
    }
}
