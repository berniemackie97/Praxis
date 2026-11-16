using System.Text.RegularExpressions;
using Praxis.Application.Inference;

namespace Praxis.Application.Agents.Capabilities;

/// <summary>Queues actions from chat. Only the owner desktop calls Resolve, which claims a request at most once.</summary>
public sealed partial class CapabilityService(ICapabilityJournal journal, ICapabilityExecutor executor)
{
    public const string Terminal = "terminal";
    public const string BrowserOpen = "browser-open";
    public const string BrewInstall = "brew-install";
    public const string NpmInstall = "npm-install";

    private readonly Lock installationGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? pendingInstallation;

    public CapabilityRequest Propose(Guid conversationId, string ownerText, string operation, string? target)
    {
        if (!OwnerAction().IsMatch(ownerText))
        {
            throw new ArgumentException("The owner needs to ask for this action in the current message.");
        }

        string description = operation switch
        {
            Terminal when !string.IsNullOrWhiteSpace(target) =>
                "Run this exact command on macOS in an empty temporary folder, with no network or access to private file contents.",
            BrowserOpen when ValidUrl(target) =>
                "Open this exact HTTPS address in your default browser. Praxis does not read or control the resulting tab.",
            BrewInstall when target is not null && InstallIntent().IsMatch(ownerText) && BrewFormula().IsMatch(target) =>
                $"Install the Homebrew core formula by running: brew install -- {target}. Uses network and writes to the existing Homebrew prefix.",
            NpmInstall when target is not null && InstallIntent().IsMatch(ownerText) && NpmVersion().IsMatch(target) =>
                $"Run npm install --global --ignore-scripts -- {target} using the public npm registry and the existing npm installation. This writes to its global prefix.",
            _ => throw new ArgumentException("Give a single HTTPS address, exact shell command, Homebrew core formula, or npm package with an exact version.")
        };

        if (target is not null && (target.Length > 1_000 || target.Any(char.IsControl)))
        {
            throw new ArgumentException("The action target must be a single line of at most 1,000 characters.");
        }

        return journal.Propose(conversationId, operation, target, description);
    }

    public IReadOnlyList<CapabilityRequest> List(int limit) => journal.List(limit);

    public CapabilityRequest? Read(Guid id) => journal.Read(id);

    public async Task<CapabilityRequest?> ResolveAsync(Guid id, bool accept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accept && journal.Read(id) is { Operation: BrewInstall or NpmInstall })
        {
            lock (installationGate)
            {
                if (lifetime.IsCancellationRequested || pendingInstallation is { IsCompleted: false })
                {
                    throw new InvalidOperationException("Wait for the current installation to finish before approving another.");
                }

                CapabilityRequest? claimed = journal.Claim(id, true);
                if (claimed is null)
                {
                    return null;
                }

                pendingInstallation = Task.Run(() => ExecuteAsync(claimed, lifetime.Token), CancellationToken.None);
                return claimed;
            }
        }

        CapabilityRequest? request = journal.Claim(id, accept);
        if (request is null || !accept)
        {
            return request;
        }

        return await ExecuteAsync(request, cancellationToken);
    }

    public async Task StopAsync()
    {
        await lifetime.CancelAsync();
        Task? running;
        lock (installationGate)
        {
            running = pendingInstallation;
        }

        if (running is not null)
        {
            await running;
        }
    }

    private async Task<CapabilityRequest?> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        CapabilityExecution result;
        try
        {
            result = await executor.ExecuteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = new CapabilityExecution(false, "The approved action was canceled.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or InferenceException or HttpRequestException)
        {
            result = new CapabilityExecution(false, exception.Message);
        }

        journal.Finish(request.Id, result);
        return journal.Read(request.Id);
    }

    private static bool ValidUrl(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && !string.IsNullOrWhiteSpace(uri.Host);

    [GeneratedRegex(@"\b(?:terminal|shell|command|run|open|visit|navigate|browser|install|set up|add)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnerAction();

    [GeneratedRegex(@"\b(?:install|set up|add)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallIntent();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9+._@-]{0,79}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BrewFormula();

    [GeneratedRegex(@"\A(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*@[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NpmVersion();
}
