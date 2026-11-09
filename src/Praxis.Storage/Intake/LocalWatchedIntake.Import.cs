using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private async Task<bool> TryImportAsync(
        LocalObservedFile file, FileStamp stamp, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            if (file.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("The file exceeds the intake byte limit.");
            }

            if (!IsStable(file))
            {
                observations.Remove(file.SourcePath);
                return false;
            }

            var digest = await HashSourceAsync(file.SourcePath, cancellationToken);
            if (!IsStable(file))
            {
                observations.Remove(file.SourcePath);
                return false;
            }

            var latest = catalog.Sources.FindLatestFile(file.SourcePath);
            if (latest?.Status == "revoked")
            {
                throw new InvalidOperationException(
                    "Revoked sources require explicit owner authorization to reimport.");
            }

            if (latest?.Sha256 != digest)
            {
                var requestKey = MakeRequestKey(
                    file.SourcePath, latest?.Revision ?? 0, digest);
                var captured = await intake.ImportFileAsync(
                    file.SourcePath, MaximumFileBytes, requestKey, cancellationToken);
                if (captured.Sha256 != digest || captured.Length != file.Length ||
                    !IsStable(file))
                {
                    throw new InvalidDataException(
                        "The source changed during capture; a new scan is required.");
                }

                verified[file.SourcePath] = new VerifiedState(stamp, now);
                errors.Remove(file.SourcePath);
                report("Captured source revision " + captured.Revision +
                    ": " + JsonSerializer.Serialize(file.SourcePath));
                return true;
            }

            verified[file.SourcePath] = new VerifiedState(stamp, now);
            errors.Remove(file.SourcePath);
        }
        catch (IOException exception)
        {
            ReportError(file.SourcePath, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportError(file.SourcePath, exception.Message);
        }
        catch (InvalidDataException exception)
        {
            ReportError(file.SourcePath, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ReportError(file.SourcePath, exception.Message);
        }

        return false;
    }

    private void ReportError(string path, string message)
    {
        if (!errors.TryGetValue(path, out var previous) || previous != message)
        {
            report("Intake error: " + JsonSerializer.Serialize(path) +
                ": " + JsonSerializer.Serialize(message));
            errors[path] = message;
        }
    }

    private static bool IsTemporaryName(string path)
    {
        var name = Path.GetFileName(path);
        return name == ".DS_Store" || name.EndsWith('~') ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".swx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStable(LocalObservedFile file)
    {
        var info = new FileInfo(file.SourcePath);
        return info.Exists &&
               (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
               info.Length == file.Length &&
               info.LastWriteTimeUtc == file.LastWriteUtc;
    }

    private static async Task<string> HashSourceAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private string MakeRequestKey(string path, int previousRevision, string digest) =>
        "watch-" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"praxis-watched-file-v2\n{path}\n{catalog.Sources.CountMovesFrom(path)}\n{previousRevision}\n{digest}")));
}
