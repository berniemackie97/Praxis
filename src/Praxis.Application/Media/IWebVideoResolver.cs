using Praxis.Domain.Media;

namespace Praxis.Application.Media;

/// <summary>Finds the online video a saved link source points to, for playback through its embed.</summary>
public interface IWebVideoResolver
{
    MediaPlaybackReference? Resolve(Guid sourceId);
}
