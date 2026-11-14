namespace Praxis.Application.Links;

/// <summary>A fetched web resource (bounded, after safe redirects), or why it was not fetched.</summary>
public sealed record FetchedPage(Uri? FinalUrl, int Status, string? ContentType, byte[]? Body, string? ReasonCode)
{
    public static FetchedPage Failed(string reasonCode, int status = 0) => new(null, status, null, null, reasonCode);
}
