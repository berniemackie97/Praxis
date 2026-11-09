using System.Globalization;
using System.Text.Json;
using Praxis.Storage;

namespace Praxis.Worker;

internal static class RecoveryCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        LocalSnapshotReport report;
        switch (args)
        {
            case ["recovery", "backup", var catalog, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(catalog) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out var maxBytes):
                report = await LocalCatalogSnapshots.BackupExistingAsync(catalog, destination, maxBytes, cancellationToken);
                break;
            case ["recovery", "verify", var backup, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && TryParseByteLimit(limit, out var maxBytes):
                report = await LocalCatalogSnapshots.VerifyAsync(backup, maxBytes, cancellationToken);
                break;
            case ["recovery", "restore", var backup, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out var maxBytes):
                report = await LocalCatalogSnapshots.RestoreAsync(backup, destination, maxBytes, cancellationToken);
                break;
            default:
                Console.Error.WriteLine("""
                    Usage:
                      Praxis.Worker recovery backup <absolute-catalog> <new-absolute-destination> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery verify <absolute-backup> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery restore <absolute-backup> <new-absolute-destination> --max-bytes <positive-byte-count>
                    """);
                return 2;
        }

        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    private static bool IsAbsolutePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool TryParseByteLimit(string text, out long limit) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit > 0;
}
