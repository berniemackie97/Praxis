namespace Praxis.Application.Links;

/// <summary>Bounded streaming download through the same public-address and redirect policy as page capture.</summary>
public interface IMediaDownloader
{
    Task<string?> DownloadAsync(Uri url, string destination, int maximumBytes, Func<Uri, bool> blocked, CancellationToken cancellationToken);
}
