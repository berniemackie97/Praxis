using Praxis.Application.Links;
using Praxis.Application.Originals;

namespace Praxis.Infrastructure.Links;

/// <summary>Captures fetched link snapshots into "Praxis Web" inside the primary intake folder.</summary>
public sealed class LinkSnapshotWriter(IOriginalStore originals, string catalogDirectory, string intakeDirectory, Action? ensureInboxApproved = null) : ILinkSnapshotWriter
{
    public const string FolderName = "Praxis Web";
    private readonly PraxisFolderWriter writer = new(originals, catalogDirectory, intakeDirectory, FolderName, ensureInboxApproved);

    public Task<string> WriteAsync(string name, string extension, byte[] content, CancellationToken cancellationToken) =>
        writer.WriteAsync(name, extension, content, cancellationToken);
}
