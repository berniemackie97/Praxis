using Praxis.Domain.Originals;

namespace Praxis.Application.Agents;

/// <summary>
/// Writes an agent-authored Markdown document into the owner's Praxis artifacts folder and
/// captures it; writing the same artifact again records a new revision of the same source.
/// </summary>
public interface IArtifactWriter
{
    Task<(string Path, CapturedOriginal Original)> WriteAsync(Guid artifactId, string title, string markdown, CancellationToken cancellationToken);
}
