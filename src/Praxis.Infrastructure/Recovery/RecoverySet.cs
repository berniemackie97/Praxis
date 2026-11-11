using System.Text.Json;
using Praxis.Domain.Common;
using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Persistence.Security.Registry;

namespace Praxis.Infrastructure.Recovery;

/// <summary>
/// A quiescent-host recovery point for the owner-local catalog, immutable originals
/// and host identity registry. Client-held credentials and application settings are external.
/// </summary>
public static class RecoverySet
{
    private const int FormatVersion = 1;
    private const long MaximumManifestBytes = 4096;

    public static async Task<RecoverySetReport> BackupExistingAsync(string catalogDirectory,
        string identityDirectory, string destinationDirectory, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        string identity = SecurityRegistrySnapshots.ValidateExistingCurrentRegistry(identityDirectory);
        using SqliteCatalog catalog = SqliteCatalog.OpenReadOnly(catalogDirectory);
        string source = FileSystemPaths.ResolveExisting(catalog.DataDirectory);
        identity = FileSystemPaths.ResolveExisting(identity);
        if (PathScope.Contains(source, identity) || PathScope.Contains(identity, source))
        {
            throw new InvalidOperationException("Catalog and identity roots must be separate directories.");
        }

        string destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, source);
        SnapshotFiles.ValidateNewDestination(destination, identity);
        using SecurityRegistry registry = SecurityRegistry.Open(identity);

        int version = catalog.Recovery.ReadStoredSchemaVersion();
        if (!SnapshotFiles.IsSnapshotSchemaVersion(version))
        {
            throw new NotSupportedException("The source catalog schema cannot be backed up.");
        }

        string staging = SnapshotFiles.CreateStagingDirectory(destination);
        CatalogSnapshotReport catalogReport = await catalog.Recovery.BackupAsync(
            Path.Combine(staging, "catalog"), maxBytes, version, cancellationToken);
        if (catalogReport.TotalBytes >= maxBytes)
        {
            throw new InvalidDataException("The recovery set exceeds its byte budget.");
        }

        SecuritySnapshotReport identityReport = await SecurityRegistrySnapshots.BackupOpenedAsync(
            registry, identity, Path.Combine(staging, "identity"),
            maxBytes - catalogReport.TotalBytes, cancellationToken);

        RecoverySetManifest manifest = new(FormatVersion, catalogReport, identityReport);
        await using (FileStream output = SnapshotFiles.OpenNewPrivateFile(Path.Combine(staging, "manifest.json")))
        {
            await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        RecoverySetReport verified = await VerifyContentsAsync(staging, maxBytes,
            allowIncomplete: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return verified;
    }

    public static Task<RecoverySetReport> VerifyAsync(string backupDirectory, long maxBytes,
        CancellationToken cancellationToken = default) =>
        VerifyContentsAsync(backupDirectory, maxBytes, allowIncomplete: false, cancellationToken);

    public static async Task<RecoverySetReport> RestoreAsync(string backupDirectory,
        string destinationDirectory, long maxBytes, CancellationToken cancellationToken = default)
    {
        RecoverySetReport expected = await VerifyAsync(backupDirectory, maxBytes, cancellationToken);
        string backup = ValidateSet(backupDirectory, allowIncomplete: false);
        string destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, backup);
        cancellationToken.ThrowIfCancellationRequested();

        string staging = SnapshotFiles.CreateStagingDirectory(destination);
        CatalogSnapshotReport catalog = await CatalogSnapshots.RestoreAsync(
            Path.Combine(backup, "catalog"), Path.Combine(staging, "catalog"), maxBytes, cancellationToken);
        SecuritySnapshotReport identity = await SecurityRegistrySnapshots.RestoreAsync(
            Path.Combine(backup, "identity"), Path.Combine(staging, "identity"),
            maxBytes - expected.Catalog.TotalBytes, cancellationToken);
        if (catalog != expected.Catalog || identity != expected.Identity)
        {
            throw new InvalidDataException("The recovered components do not match their snapshot.");
        }

        using (SecurityRegistry recovered = SecurityRegistry.Open(Path.Combine(staging, "identity")))
        {
            if (recovered.InstallationId != expected.Identity.InstallationId)
            {
                throw new InvalidDataException("The recovered installation identity changed.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return expected;
    }

    private static async Task<RecoverySetReport> VerifyContentsAsync(string backupDirectory,
        long maxBytes, bool allowIncomplete, CancellationToken cancellationToken)
    {
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        string root = ValidateSet(backupDirectory, allowIncomplete);
        string path = Path.Combine(root, "manifest.json");
        long length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("The recovery set manifest size is invalid.");
        }

        RecoverySetManifest manifest;
        try
        {
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest = await JsonSerializer.DeserializeAsync<RecoverySetManifest>(
                input, cancellationToken: cancellationToken) ??
                throw new InvalidDataException("The recovery set manifest is missing.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The recovery set manifest is malformed.", exception);
        }

        if (manifest.FormatVersion != FormatVersion || manifest.Catalog is null ||
            manifest.Identity is null || manifest.Catalog.TotalBytes <= 0 ||
            manifest.Identity.DatabaseBytes <= 0 ||
            manifest.Identity.DatabaseBytes > maxBytes ||
            manifest.Catalog.TotalBytes > maxBytes - manifest.Identity.DatabaseBytes)
        {
            throw new InvalidDataException("The recovery set manifest is incompatible.");
        }

        CatalogSnapshotReport catalog = await CatalogSnapshots.VerifyAsync(
            Path.Combine(root, "catalog"), maxBytes, cancellationToken);
        SecuritySnapshotReport identity = await SecurityRegistrySnapshots.VerifyAsync(
            Path.Combine(root, "identity"), maxBytes - catalog.TotalBytes, cancellationToken);
        if (catalog != manifest.Catalog || identity != manifest.Identity)
        {
            throw new InvalidDataException("The recovery set manifest does not match its components.");
        }

        return new RecoverySetReport(catalog, identity);
    }

    private static string ValidateSet(string directory, bool allowIncomplete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The recovery set path must be absolute.", nameof(directory));
        }

        string root = SnapshotFiles.CheckPrivateDirectory(Path.GetFullPath(directory));
        if (!allowIncomplete &&
            Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Contains(".incomplete-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("An incomplete recovery set is not a backup.");
        }

        HashSet<string> names = new(StringComparer.Ordinal) { "manifest.json", "catalog", "identity" };
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(entry);
            if (!names.Remove(name) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                (name == "manifest.json" ? !File.Exists(entry) : !Directory.Exists(entry)))
            {
                throw new InvalidDataException("The recovery set contains a missing, linked or unexpected entry.");
            }
        }

        if (names.Count != 0)
        {
            throw new InvalidDataException("The recovery set is incomplete.");
        }

        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(Path.Combine(root, "manifest.json")) & ~SnapshotFiles.PrivateFile) != 0)
        {
            throw new UnauthorizedAccessException("Recovery set manifests must be owner-private.");
        }

        return root;
    }
}
