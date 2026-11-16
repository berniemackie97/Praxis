using System.Text.RegularExpressions;
using Praxis.Application.Inference;

namespace Praxis.Application.Agents.Capabilities;

/// <summary>Queues actions from chat. Only the owner desktop calls Resolve, which claims a request at most once.</summary>
public sealed partial class CapabilityService(ICapabilityJournal journal, ICapabilityExecutor executor)
{
    public const string Terminal = "terminal";
    public const string BrowserOpen = "browser-open";

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
            _ => throw new ArgumentException("Choose a supported action with a valid target. Browser addresses must be HTTPS.")
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
        CapabilityRequest? request = journal.Claim(id, accept);
        if (request is null || !accept)
        {
            return request;
        }

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

        journal.Finish(id, result);
        return journal.Read(id);
    }

    private static bool ValidUrl(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && !string.IsNullOrWhiteSpace(uri.Host);

    [GeneratedRegex(@"\b(?:terminal|shell|command|run|open|visit|navigate|browser)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnerAction();
}
