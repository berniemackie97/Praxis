namespace Praxis.Application.Links;

/// <summary>Turns a link to an online video into a snapshot with public metadata and published captions.</summary>
public interface IVideoLinkResolver
{
    /// <summary>True when the URL is a video page this resolver understands.</summary>
    bool Handles(Uri url);

    /// <summary>The snapshot, or null when the video is unavailable.</summary>
    Task<WebVideoSnapshot?> ResolveAsync(Uri url, CancellationToken cancellationToken);
}
