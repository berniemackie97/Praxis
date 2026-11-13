namespace Praxis.Domain.Agents;

/// <summary>
/// An owner-configured chat backend. <see cref="BackendId"/> is the provider identity disclosure
/// rules name. API keys are referenced by Keychain account, never stored in the catalog. A
/// backend is local only when its endpoint is loopback.
/// </summary>
public sealed record AgentBackendProfile(string BackendId, string Kind, string? Endpoint, string Model, string? KeyAccount, string? Executable, bool Enabled)
{
    public bool IsLocal => Kind == AgentBackendKinds.OpenAiCompatible && Endpoint is not null &&
                           Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BackendId) || BackendId.Length > 64 || !BackendId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            !AgentBackendKinds.All.Contains(Kind, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(Model) || Model.Length > 200 ||
            KeyAccount?.Length > 128)
        {
            throw new ArgumentException("The agent backend profile is invalid.");
        }

        bool http = Kind is AgentBackendKinds.OpenAiCompatible or AgentBackendKinds.Anthropic;
        if (http && (Endpoint is null || !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) ||
                     (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))))
        {
            throw new ArgumentException("HTTP backends need an HTTPS endpoint, or HTTP on loopback.");
        }

        if (AgentBackendKinds.IsSession(Kind) && (Executable is null || !Path.IsPathFullyQualified(Executable)))
        {
            throw new ArgumentException("A CLI backend needs an absolute executable path.");
        }
    }
}
