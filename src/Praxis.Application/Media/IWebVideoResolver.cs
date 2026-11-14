using Praxis.Domain.Media;

namespace Praxis.Application.Media;

/// <summary>Finds the online video a saved source points to, for playback through its embed.</summary>
public interface IWebVideoResolver
{
    Task<MediaPlaybackReference?> ResolveAsync(Guid sourceId, CancellationToken cancellationToken);
}
