using System.Buffers;
using System.Security.Cryptography;

namespace Praxis.Storage;

internal readonly record struct OriginalObject(string Digest, long Length);

internal sealed class OriginalFileStore(string dataDirectory)
{
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
                    // A concurrent publication must still pass the same content check.
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

        var objects = CheckDirectory(Path.Combine(dataDirectory, "objects"));
        var directory = CheckDirectory(Path.Combine(objects, original.Digest[..2]));
        directory = CheckDirectory(Path.Combine(directory, original.Digest[2..4]));
        await VerifyAsync(Path.Combine(directory, original.Digest),
            original.Digest, original.Length, cancellationToken);
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

    private static void ValidateDigest(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length != 64 || digest.Any(character =>
            character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
        {
            throw new ArgumentException("An original digest must be lowercase SHA-256.", nameof(digest));
        }
    }
}
