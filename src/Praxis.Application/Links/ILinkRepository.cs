using Praxis.Domain.Processing;

namespace Praxis.Application.Links;

/// <summary>Saved-link fetch jobs, their snapshots and the owner's link settings.</summary>
public interface ILinkRepository
{
    /// <summary>The leased link, or null when the lease is not held or the source is not a readable saved link.</summary>
    LeasedLink? ReadLeased(ProcessingLease lease);

    /// <summary>Records the captured snapshot and completes the job.</summary>
    bool Complete(ProcessingLease lease, Uri finalUrl, int status, string? contentType, string snapshotLocator);

    /// <summary>Records why nothing was fetched and closes the job.</summary>
    bool Close(ProcessingLease lease, string reasonCode, int status = 0);

    LinkSettings ReadSettings();

    void SaveSettings(LinkSettings settings);
}
