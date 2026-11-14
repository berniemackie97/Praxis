namespace Praxis.Application.Links;

/// <summary>Writes a fetched snapshot into the owner's "Praxis Web" folder and captures it as a source.</summary>
public interface ILinkSnapshotWriter
{
    /// <summary>Writes and captures the snapshot; returns the path it was written to.</summary>
    Task<string> WriteAsync(string name, string extension, byte[] content, CancellationToken cancellationToken);
}
