namespace Praxis.Application.Links;

/// <summary>
/// Fetches a public web resource for the owner: http(s) only, public addresses only (re-checked on
/// every redirect and pinned per connection), no cookies or credentials, bounded size and time.
/// </summary>
public interface ILinkFetcher
{
    Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken);
}
