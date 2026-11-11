using Praxis.Domain.Sources;

namespace Praxis.Application.Sources;

/// <summary>Identity and lifecycle of captured sources: provenance, path lookup, observed relocations and revocation.</summary>
public interface ISourceRepository
{
    /// <summary>
    /// Canonical, source-scoped provenance for one recorded revision (the latest when null).
    /// Revoked and older revisions remain inspectable to the trusted local owner, not searchable evidence.
    /// </summary>
    SourceProvenance? ReadProvenance(Guid sourceId, int? revision);

    /// <summary>All committed receipt routes for one source, including legacy unattributed entries.</summary>
    IReadOnlyList<CaptureReceipt> ListCaptureReceipts(Guid sourceId, int? revision, int limit);

    /// <summary>Versioned processing status for all registered processors of one source.</summary>
    IReadOnlyList<SourceProcessingState> ListProcessing(Guid sourceId, int? revision, int limit);

    /// <summary>The latest recorded revision of the file source stored under <paramref name="path"/>.</summary>
    SourceFileVersion? FindLatestFile(string path);

    /// <summary>Latest revision of an active file source, or null when unavailable.</summary>
    CurrentFileVersion? CurrentFile(Guid sourceId);

    /// <summary>
    /// Revokes an active source and cancels its unfinished processing in one transaction.
    /// Originals and history stay immutable; derived reads filter on source status.
    /// </summary>
    bool Revoke(Guid sourceId);

    /// <summary>Active file sources with their stored locator and current digest.</summary>
    IReadOnlyList<(Guid SourceId, string Locator, string Digest)> ActiveFileLocators();

    /// <summary>
    /// Folds an active source recorded under a non-canonical spelling into the source that already
    /// uses the canonical spelling: collection memberships and proposal reviews carry over to
    /// matching proposals, then the alias is revoked. Returns false when nothing changed.
    /// </summary>
    bool FoldAlias(Guid aliasSourceId, string canonicalLocator);

    IReadOnlyList<SourceMovement> ListMovements(Guid sourceId, int limit);

    long CountMovesFrom(string previousPath);

    /// <summary>Moves an active file source to a new path when its current digest matches the verified bytes there.</summary>
    bool RelocateObservedFile(string previousPath, string currentPath, string verifiedDigest);
}
