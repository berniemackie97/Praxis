using System.Buffers;
using System.Security.Cryptography;

namespace Praxis.Storage;

internal readonly record struct OriginalObject(string Digest, long Length);

internal sealed class OriginalFileStore
{
    private readonly string dataDirectory;

    public OriginalFileStore(string dataDirectory) => this.dataDirectory = dataDirectory;

    private const int BufferLength = 64 * 1024;
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<OriginalObject> PublishAsync(
        Stream input, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The original content stream must be readable.", nameof(input));
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var staging = EnsureDirectory(Path.Combine(dataDirectory, "staging"));
        var objects = EnsureDirectory(Path.Combine(dataDirectory, "objects"));
        var pending = Path.Combine(staging, $"{Guid.NewGuid():N}.part");
        try
        {
            var length = 0L;
            string digest;
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferLength,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var output = new FileStream(pending, options))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferLength);
                    try
                    {
                        while (true)
                        {
                            var count = await input.ReadAsync(buffer.AsMemory(0, BufferLength), cancellationToken);
                            if (count == 0)
                            {
                                break;
                            }

                            if (count > maxBytes - length)
                            {
                                throw new InvalidDataException("The original exceeds the authorized byte limit.");
                            }

                            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                            hash.AppendData(buffer, 0, count);
                            length += count;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    }
                }

                digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            await VerifyAsync(pending, digest, length, cancellationToken);
            var directory = EnsureDirectory(Path.Combine(objects, digest[..2]));
            directory = EnsureDirectory(Path.Combine(directory, digest[2..4]));
            var published = Path.Combine(directory, digest);
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(published))
            {
                try
                {
                    File.Move(pending, published);
                }
                catch (IOException) when (File.Exists(published))
                {
                    await VerifyAsync(published, digest, length, cancellationToken);
                    return new OriginalObject(digest, length);
                }
            }

            await VerifyAsync(published, digest, length, cancellationToken);
            return new OriginalObject(digest, length);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    public async Task VerifyAsync(OriginalObject original, CancellationToken cancellationToken)
    {
        ValidateDigest(original.Digest);
        if (original.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(original));
        }

        await VerifyAsync(ExistingObjectPath(original.Digest),
            original.Digest, original.Length, cancellationToken);
    }

    internal async Task<byte[]> ReadVerifiedBytesAsync(
        OriginalObject original, int maxBytes, CancellationToken cancellationToken)
    {
        ValidateDigest(original.Digest);
        if (original.Length < 0 || original.Length > maxBytes)
        {
            throw new InvalidDataException("The original exceeds the extraction byte budget.");
        }

        var path = ExistingObjectPath(original.Digest);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An original object path is a symbolic link.");
        }

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != original.Length)
        {
            throw new InvalidDataException("An original object has a conflicting length.");
        }

        var bytes = new byte[(int)original.Length];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(bytes), Convert.FromHexString(original.Digest)))
        {
            throw new InvalidDataException("An original object failed integrity verification.");
        }

        return bytes;
    }

    public async Task CopyVerifiedAsync(
        OriginalObject original, OriginalFileStore destination,
        CancellationToken cancellationToken)
    {
        ValidateDigest(original.Digest);
        if (original.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(original));
        }

        var sourcePath = ExistingObjectPath(original.Digest);
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An original object path is a symbolic link.");
        }

        var objects = EnsureDirectory(Path.Combine(destination.dataDirectory, "objects"));
        var first = EnsureDirectory(Path.Combine(objects, original.Digest[..2]));
        var second = EnsureDirectory(Path.Combine(first, original.Digest[2..4]));
        var targetPath = Path.Combine(second, original.Digest);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferLength,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        await using (var input = new FileStream(sourcePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, BufferLength,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(targetPath, options))
        {
            await input.CopyToAsync(output, BufferLength, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        await destination.VerifyAsync(original, cancellationToken);
    }

    private string ExistingObjectPath(string digest)
    {
        var objects = CheckDirectory(Path.Combine(dataDirectory, "objects"));
        var first = CheckDirectory(Path.Combine(objects, digest[..2]));
        var second = CheckDirectory(Path.Combine(first, digest[2..4]));
        return Path.Combine(second, digest);
    }

    public IEnumerable<string> EnumeratePublishedDigests()
    {
        var root = Path.Combine(dataDirectory, "objects");
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            yield break;
        }

        CheckDirectory(root);
        foreach (var first in Directory.EnumerateFileSystemEntries(root))
        {
            CheckDirectory(first);
            foreach (var second in Directory.EnumerateFileSystemEntries(first))
            {
                CheckDirectory(second);
                foreach (var file in Directory.EnumerateFileSystemEntries(second))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                        !File.Exists(file))
                    {
                        throw new InvalidDataException("The object inventory contains an unexpected file.");
                    }

                    var digest = Path.GetFileName(file);
                    ValidateDigest(digest);
                    if (digest[..2] != Path.GetFileName(first) ||
                        digest[2..4] != Path.GetFileName(second))
                    {
                        throw new InvalidDataException("An object is outside its expected digest directory.");
                    }

                    yield return digest;
                }
            }
        }
    }

    public int CountStagedFiles()
    {
        var staging = Path.Combine(dataDirectory, "staging");
        if (!Directory.Exists(staging) && !File.Exists(staging))
        {
            return 0;
        }

        CheckDirectory(staging);
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(entry) ||
                Path.GetExtension(entry) != ".part")
            {
                throw new InvalidDataException("The staging inventory contains an unexpected entry.");
            }

            count = checked(count + 1);
        }

        return count;
    }

    private static async Task VerifyAsync(
        string path, string digest, long length, CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("An original object path is a symbolic link.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != length ||
            !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(stream, cancellationToken),
                Convert.FromHexString(digest)))
        {
            throw new InvalidDataException("An original object failed integrity verification.");
        }
    }

    private static string EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
        }

        return CheckDirectory(path);
    }

    private static string CheckDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The original storage path contains a symbolic link.");
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & ~PrivateDirectoryMode) != 0)
        {
            throw new UnauthorizedAccessException("Original storage directories must be private.");
        }

        return path;
    }

    internal static bool IsValidDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateDigest(string digest)
    {
        if (!IsValidDigest(digest))
        {
            throw new ArgumentException("An original digest must be lowercase SHA-256.", nameof(digest));
        }
    }
}
