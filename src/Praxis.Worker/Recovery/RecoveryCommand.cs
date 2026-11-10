using System.Globalization;
using System.Text.Json;
using Praxis.Storage.Recovery;
using Praxis.Storage.Security;

namespace Praxis.Worker.Recovery;

internal static class RecoveryCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        object report;
        switch (args)
        {
            case ["recovery", "backup", var catalog, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(catalog) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out long maxBytes):
                report = await LocalCatalogSnapshots.BackupExistingAsync(catalog, destination, maxBytes, cancellationToken);
                break;
            case ["recovery", "verify", var backup, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && TryParseByteLimit(limit, out long maxBytes):
                report = await LocalCatalogSnapshots.VerifyAsync(backup, maxBytes, cancellationToken);
                break;
            case ["recovery", "restore", var backup, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out long maxBytes):
                report = await LocalCatalogSnapshots.RestoreAsync(backup, destination, maxBytes, cancellationToken);
                break;
            case ["recovery", "identity-backup", var registry, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(registry) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out long maxBytes):
                report = await SecurityRegistrySnapshots.BackupExistingAsync(registry, destination, maxBytes, cancellationToken);
                break;
            case ["recovery", "identity-verify", var backup, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && TryParseByteLimit(limit, out long maxBytes):
                report = await SecurityRegistrySnapshots.VerifyAsync(backup, maxBytes, cancellationToken);
                break;
            case ["recovery", "identity-restore", var backup, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out long maxBytes):
                report = await SecurityRegistrySnapshots.RestoreAsync(backup, destination, maxBytes, cancellationToken);
                break;
            case ["recovery", "set-backup", var catalog, var identity, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(catalog) && IsAbsolutePath(identity) && IsAbsolutePath(destination) &&
                     TryParseByteLimit(limit, out long maxBytes):
                report = await LocalRecoverySet.BackupExistingAsync(catalog, identity, destination, maxBytes, cancellationToken);
                break;
            case ["recovery", "set-verify", var backup, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && TryParseByteLimit(limit, out long maxBytes):
                report = await LocalRecoverySet.VerifyAsync(backup, maxBytes, cancellationToken);
                break;
            case ["recovery", "set-restore", var backup, var destination, "--max-bytes", var limit]
                when IsAbsolutePath(backup) && IsAbsolutePath(destination) && TryParseByteLimit(limit, out long maxBytes):
                report = await LocalRecoverySet.RestoreAsync(backup, destination, maxBytes, cancellationToken);
                break;
            default:
                Console.Error.WriteLine("""
                    Usage:
                      Praxis.Worker recovery backup <absolute-catalog> <new-absolute-destination> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery verify <absolute-backup> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery restore <absolute-backup> <new-absolute-destination> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery identity-backup <absolute-registry> <new-absolute-backup> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery identity-verify <absolute-backup> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery identity-restore <absolute-backup> <new-absolute-registry> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery set-backup <absolute-catalog> <absolute-identity> <new-absolute-set> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery set-verify <absolute-set> --max-bytes <positive-byte-count>
                      Praxis.Worker recovery set-restore <absolute-set> <new-absolute-destination> --max-bytes <positive-byte-count>
                    """);
                return 2;
        }

        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    private static bool IsAbsolutePath(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool TryParseByteLimit(string text, out long limit) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit > 0;
}
