namespace Praxis.Inference;

/// <summary>
/// Validates endpoints for local runtimes. Only loopback HTTP(S) origins are accepted, so a
/// local-model configuration can never send source content to another machine.
/// </summary>
public static class LoopbackEndpoint
{
    public static Uri Validate(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (endpoint.Length > 256 || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("Local model endpoints must be a loopback http(s) origin such as http://127.0.0.1:11434.", nameof(endpoint));
        }

        return uri;
    }
}
