using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Recovery;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

/// <summary>
/// Offline identity-registry backup, independent verification and non-overwriting restore.
/// This does not include client-held private keys, recovery secrets or the content catalog.
/// </summary>
public static class SecurityRegistrySnapshots
{
    public static async Task<SecuritySnapshotReport> BackupExistingAsync(string registryDirectory,
        string destinationDirectory, long maxBytes, CancellationToken cancellationToken = default)
    {
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        string source = ValidateExistingCurrentRegistry(registryDirectory);
        using SecurityRegistry registry = SecurityRegistry.Open(source);
        return await BackupOpenedAsync(registry, source, destinationDirectory, maxBytes, cancellationToken);
    }

    internal static string ValidateExistingCurrentRegistry(string registryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDirectory);
        if (!Path.IsPathFullyQualified(registryDirectory))
        {
            throw new ArgumentException("The registry directory must be absolute.", nameof(registryDirectory));
        }

        string source = SnapshotFiles.CheckPrivateDirectory(Path.GetFullPath(registryDirectory));
        string databasePath = Path.Combine(source, "identity.sqlite");
        if (!File.Exists(databasePath) || (File.GetAttributes(databasePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An existing security registry database is required.");
        }

        using SqliteConnection check = SecuritySnapshotFiles.OpenReadOnly(databasePath);
        check.Open();
        using SqliteCommand version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(version.ExecuteScalar()) != SecurityRegistrySchema.Version)
        {
            throw new NotSupportedException("Upgrade the security registry explicitly before backing it up.");
        }

        return source;
    }

    internal static async Task<SecuritySnapshotReport> BackupOpenedAsync(SecurityRegistry registry,
        string source, string destinationDirectory, long maxBytes, CancellationToken cancellationToken)
    {
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, source);
        string staging = SnapshotFiles.CreateStagingDirectory(destination);
        string snapshotPath = Path.Combine(staging, "identity.sqlite");

        using (SqliteConnection snapshot = new(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            snapshot.Open();
            registry.Execute(connection =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SnapshotFiles.EnsureCatalogFitsBudget(connection, maxBytes);
                connection.BackupDatabase(snapshot);
                return 0;
            });

            using SqliteCommand journal = snapshot.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            if (!string.Equals(journal.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The registry snapshot could not be finalized.");
            }
        }

        SnapshotFiles.RestrictFile(snapshotPath);
        (long length, string digest) = await SnapshotFiles.HashFileAsync(snapshotPath, cancellationToken);
        if (length > maxBytes)
        {
            throw new InvalidDataException("The security registry exceeds the backup byte budget.");
        }

        SecuritySnapshotManifest manifest = new(1, SecurityRegistrySchema.Version, registry.InstallationId,
            length, digest);
        await using (FileStream stream = SnapshotFiles.OpenNewPrivateFile(Path.Combine(staging, "manifest.json")))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        SecuritySnapshotReport report = await VerifyContentsAsync(staging, maxBytes, allowIncomplete: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return report;
    }

    public static Task<SecuritySnapshotReport> VerifyAsync(string backupDirectory, long maxBytes,
        CancellationToken cancellationToken = default) =>
        VerifyContentsAsync(backupDirectory, maxBytes, allowIncomplete: false, cancellationToken);

    private static async Task<SecuritySnapshotReport> VerifyContentsAsync(string backupDirectory,
        long maxBytes, bool allowIncomplete, CancellationToken cancellationToken)
    {
        SnapshotFiles.ValidateBudget(maxBytes);
        string backup = SecuritySnapshotFiles.ValidateBackup(backupDirectory, allowIncomplete);
        SecuritySnapshotManifest manifest = SecuritySnapshotFiles.ReadManifest(backup);
        if (manifest.DatabaseLength > maxBytes)
        {
            throw new InvalidDataException("The security registry exceeds the verification byte budget.");
        }

        string database = Path.Combine(backup, "identity.sqlite");
        (long length, string digest) = await SnapshotFiles.HashFileAsync(database, cancellationToken,
            manifest.DatabaseLength);
        if (length != manifest.DatabaseLength || digest != manifest.DatabaseSha256)
        {
            throw new InvalidDataException("The security registry snapshot digest is invalid.");
        }

        SecuritySnapshotFiles.VerifyDatabase(database, manifest);
        return new SecuritySnapshotReport(manifest.InstallationId, length, digest);
    }

    public static async Task<SecuritySnapshotReport> RestoreAsync(string backupDirectory,
        string destinationDirectory, long maxBytes, CancellationToken cancellationToken = default)
    {
        string backup = SecuritySnapshotFiles.ValidateBackup(backupDirectory);
        string destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, backup);
        SecuritySnapshotReport verified = await VerifyAsync(backup, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string staging = SnapshotFiles.CreateStagingDirectory(destination);
        SecuritySnapshotManifest manifest = SecuritySnapshotFiles.ReadManifest(backup);
        await SnapshotFiles.CopyCheckedFileAsync(Path.Combine(backup, "identity.sqlite"),
            Path.Combine(staging, "identity.sqlite"), manifest.DatabaseLength,
            manifest.DatabaseSha256, cancellationToken);
        await using (FileStream output = SnapshotFiles.OpenNewPrivateFile(Path.Combine(staging, "manifest.json")))
        {
            await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        await VerifyContentsAsync(staging, maxBytes, allowIncomplete: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // A live registry accepts only identity.sqlite and its own SQLite/lease files.
        File.Delete(Path.Combine(staging, "manifest.json"));
        Directory.Move(staging, destination);
        return verified;
    }
}
