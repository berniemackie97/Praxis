namespace Praxis.Storage;

public sealed record LocalOriginalExport(Guid ItemId, int Revision, string Sha256, long Length);

public sealed partial class LocalCatalog
{
    public async Task<LocalOriginalExport> ExportOriginalAsync(
        Guid itemId, int revision, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item ID and latest revision are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination must be absolute.", nameof(destinationPath));
        }

        var destination = Path.GetFullPath(destinationPath);
        var catalogRoot = Path.GetDirectoryName(DatabasePath)!;
        var relative = Path.GetRelativePath(catalogRoot, destination);
        if (relative is "." or ".." ||
            (!relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) && !Path.IsPathRooted(relative)))
        {
            throw new InvalidOperationException("An export cannot be written inside the private catalog.");
        }

        var folder = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(folder) ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The chosen export directory is unavailable or linked.");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("The export destination already exists and cannot be overwritten.");
        }

        OriginalObject original;
        lock (connection)
        {
            original = CurrentExportableOriginal(itemId, revision)
                ?? throw new InvalidOperationException("The selected source version is not active or current.");
        }

        var pending = Path.Combine(folder, ".praxis-export-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var output = new FileStream(pending, options))
            {
                await new OriginalFileStore(catalogRoot)
                    .CopyVerifiedAsync(original, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (connection)
            {
                if (CurrentExportableOriginal(itemId, revision) != original)
                {
                    throw new InvalidOperationException("The source changed or was revoked during export.");
                }

                File.Move(pending, destination);
            }

            return new LocalOriginalExport(itemId, revision, original.Digest, original.Length);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private OriginalObject? CurrentExportableOriginal(Guid itemId, int revision)
    {
        using var query = Command(connection, """
            SELECT v.digest, o.byte_length
            FROM item_versions v
            JOIN items i ON i.item_id = v.item_id
            JOIN sources s ON s.source_id = i.source_id
            JOIN original_objects o ON o.digest = v.digest
            WHERE v.item_id = $item AND v.revision = $revision
              AND s.status = 'active'
              AND v.revision = (
                  SELECT MAX(latest.revision) FROM item_versions latest
                  WHERE latest.item_id = v.item_id)
            """);
        query.Parameters.AddWithValue("$item", itemId.ToString("D"));
        query.Parameters.AddWithValue("$revision", revision);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new OriginalObject(reader.GetString(0), reader.GetInt64(1)) : null;
    }
}
