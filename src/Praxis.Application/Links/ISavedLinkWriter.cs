namespace Praxis.Application.Links;

/// <summary>Saves a link the owner asked for as a link file in the intake folder, which the link stage then fetches.</summary>
public interface ISavedLinkWriter
{
    /// <summary>Writes and captures the link file; returns its path.</summary>
    Task<string> SaveAsync(Uri url, string? title, CancellationToken cancellationToken);
}
