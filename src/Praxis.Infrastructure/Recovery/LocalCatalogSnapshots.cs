using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Originals;

namespace Praxis.Infrastructure.Recovery;

public sealed record LocalSnapshotReport(int ObjectCount, long TotalBytes, string CatalogSha256);

/// <summary>
/// Coherent catalog-plus-originals snapshots that do not require an open catalog session:
/// verification, staged restore into a new directory, and backup of a closed catalog.
/// </summary>
public static class LocalCatalogSnapshots
{
    public static async Task<LocalSnapshotReport> BackupExistingAsync(string dataDirectory, string destinationDirectory, long maxBytes, CancellationToken cancellationToken = default)
    {
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        using LocalCatalog catalog = LocalCatalog.OpenReadOnly(dataDirectory);
        int version = catalog.Recovery.ReadStoredSchemaVersion();
        if (!SnapshotFiles.IsSnapshotSchemaVersion(version))
        {
            throw new NotSupportedException("The source catalog schema is not supported for snapshots.");
        }

        return await catalog.Recovery.BackupAsync(destinationDirectory, maxBytes, version, cancellationToken);
    }

    public static async Task<LocalSnapshotReport> RestoreAsync(string backupDirectory, string destinationDirectory, long maxBytes, CancellationToken cancellationToken = default)
    {
        string backup = SnapshotFiles.ValidateExistingBackup(backupDirectory);
        string destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, backup);
        SnapshotFiles.ValidateBudget(maxBytes);
        LocalSnapshotManifest expected = SnapshotFiles.ReadSnapshotManifest(backup);
        LocalSnapshotReport verified = await SnapshotFiles.VerifyContentsAsync(backup, expected, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string staging = SnapshotFiles.CreateStagingDirectory(destination);
        await SnapshotFiles.CopyCheckedFileAsync(Path.Combine(backup, "catalog.sqlite"), Path.Combine(staging, "catalog.sqlite"), expected.CatalogLength, expected.CatalogSha256, cancellationToken);

        OriginalFileStore source = new(backup);
        OriginalFileStore target = new(staging);
        foreach (LocalSnapshotObject original in expected.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyVerifiedToStoreAsync(new OriginalObject(original.Digest, original.Length), target, cancellationToken);
        }

        await SnapshotFiles.VerifyContentsAsync(staging, expected, maxBytes, cancellationToken);
        using (LocalCatalog restored = LocalCatalog.Open(staging))
        {
            OriginalRecoveryInspection inspection = await restored.Recovery.InspectAsync(cancellationToken);
            if (inspection.MissingObjects != 0 || inspection.CorruptObjects != 0)
            {
                throw new InvalidDataException("The restored catalog has invalid originals.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return verified;
    }

    public static async Task<LocalSnapshotReport> VerifyAsync(string backupDirectory, long maxBytes, CancellationToken cancellationToken = default)
    {
        string backup = SnapshotFiles.ValidateExistingBackup(backupDirectory);
        SnapshotFiles.ValidateBudget(maxBytes);
        LocalSnapshotManifest manifest = SnapshotFiles.ReadSnapshotManifest(backup);
        return await SnapshotFiles.VerifyContentsAsync(backup, manifest, maxBytes, cancellationToken);
    }
}
