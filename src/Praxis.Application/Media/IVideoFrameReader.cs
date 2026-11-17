using Praxis.Application.Links;

namespace Praxis.Application.Media;

public interface IVideoFrameReader
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<WebVideoFrame>> ReadAsync(string path, int maximumFrames, CancellationToken cancellationToken);
}
