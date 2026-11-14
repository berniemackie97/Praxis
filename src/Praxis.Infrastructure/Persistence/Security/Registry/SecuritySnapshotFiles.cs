using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence.Recovery;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

internal static class SecuritySnapshotFiles
{
    internal static string ValidateBackup(string directory, bool allowIncomplete = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The backup directory must be absolute.", nameof(directory));
        }

        string root = SnapshotFiles.CheckPrivateDirectory(Path.GetFullPath(directory));
        if (!allowIncomplete && Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Contains(".incomplete-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("An incomplete staging directory is not a backup.");
        }

        HashSet<string> expected = new(StringComparer.Ordinal) { "identity.sqlite", "manifest.json" };
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (FinderMetadata.IsBenign(entry))
            {
                continue;
            }

            if (!expected.Remove(Path.GetFileName(entry)) ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || !File.Exists(entry))
            {
                throw new InvalidDataException("The security backup contains an unexpected or linked entry.");
            }

            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(entry) & ~SnapshotFiles.PrivateFile) != 0)
            {
                throw new UnauthorizedAccessException("Security backup files must be owner-private.");
            }
        }

        if (expected.Count != 0)
        {
            throw new InvalidDataException("The security backup is incomplete.");
        }

        return root;
    }

    internal static SecuritySnapshotManifest ReadManifest(string backup)
    {
        string path = Path.Combine(backup, "manifest.json");
        if (new FileInfo(path).Length is <= 0 or > 4096)
        {
            throw new InvalidDataException("The security backup manifest size is invalid.");
        }

        SecuritySnapshotManifest manifest;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest = JsonSerializer.Deserialize<SecuritySnapshotManifest>(stream) ??
                throw new InvalidDataException("The security backup manifest is missing.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The security backup manifest is malformed.", exception);
        }

        if (manifest.FormatVersion != 1 || manifest.SchemaVersion != SecurityRegistrySchema.Version ||
            manifest.InstallationId == Guid.Empty || manifest.DatabaseLength <= 0 ||
            !OriginalFileStore.IsValidDigest(manifest.DatabaseSha256))
        {
            throw new InvalidDataException("The security backup manifest is incompatible.");
        }

        return manifest;
    }

    internal static SqliteConnection OpenReadOnly(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());

    internal static void VerifyDatabase(string path, SecuritySnapshotManifest manifest)
    {
        try
        {
            using SqliteConnection connection = OpenReadOnly(path);
            connection.Open();
            using (SqliteCommand integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check";
                if (integrity.ExecuteScalar() is not string result || result != "ok")
                {
                    throw new InvalidDataException("The security backup database failed integrity verification.");
                }
            }

            using (SqliteCommand version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(version.ExecuteScalar()) != manifest.SchemaVersion)
                {
                    throw new InvalidDataException("The security backup database schema version differs from its manifest.");
                }
            }

            SecurityRegistrySchema.VerifySchema(connection);

            using SqliteCommand installation = connection.CreateCommand();
            installation.CommandText = "SELECT installation_id FROM installation WHERE singleton=1";
            if (installation.ExecuteScalar() is not string text ||
                !Guid.TryParse(text, out Guid id) || id != manifest.InstallationId)
            {
                throw new InvalidDataException("The security backup installation identity differs from its manifest.");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The security backup database could not be verified.", exception);
        }
    }
}
