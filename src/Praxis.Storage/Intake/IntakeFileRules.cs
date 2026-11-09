using System.Security.Cryptography;

namespace Praxis.Storage;

/// <summary>Limits and file checks shared by watched-folder capture and rename correlation.</summary>
internal static class IntakeFileRules
{
    internal const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Partial downloads, editor swaps and Finder metadata wait for an ordinary name.</summary>
    internal static bool IsTemporaryName(string path)
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

    /// <summary>True when the file still has the scanned length and timestamp and is not a link.</summary>
    internal static bool IsStable(LocalObservedFile file)
    {
        var info = new FileInfo(file.SourcePath);
        return info.Exists &&
               (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
               info.Length == file.Length &&
               info.LastWriteTimeUtc == file.LastWriteUtc;
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
