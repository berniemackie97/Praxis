using System.Buffers;
using System.Security.Cryptography;
using Praxis.Domain.Originals;

namespace Praxis.Infrastructure.FileSystem;

internal sealed class OriginalFileStore
{
    private readonly string dataDirectory;

    public OriginalFileStore(string dataDirectory) => this.dataDirectory = dataDirectory;

    private const int BufferLength = 64 * 1024;
    private static readonly UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<OriginalObject> PublishAsync(Stream input, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The original content stream must be readable.", nameof(input));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        string staging = EnsureDirectory(Path.Combine(dataDirectory, "staging"));
        string objects = EnsureDirectory(Path.Combine(dataDirectory, "objects"));
        string pending = Path.Combine(staging, $"{Guid.NewGuid():N}.part");
        try
        {
            long length = 0L;
            string digest;
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (FileStream output = CreatePrivateFile(pending))
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferLength);
                    try
                    {
                        while (true)
                        {
                            int count = await input.ReadAsync(buffer.AsMemory(0, BufferLength), cancellationToken);
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
            string directory = EnsureDirectory(Path.Combine(objects, digest[..2]));
            directory = EnsureDirectory(Path.Combine(directory, digest[2..4]));
            string published = Path.Combine(directory, digest);
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

        await VerifyAsync(ExistingObjectPath(original.Digest), original.Digest, original.Length, cancellationToken);
    }

    internal async Task<byte[]> ReadVerifiedBytesAsync(OriginalObject original, int maxBytes, CancellationToken cancellationToken)
    {
        ValidateDigest(original.Digest);
        if (original.Length < 0 || original.Length > maxBytes)
        {
            throw new InvalidDataException("The original exceeds the extraction byte budget.");
        }

        await using FileStream input = OpenOriginal(original);
        byte[] bytes = new byte[(int)original.Length];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        if (input.Length != original.Length)
        {
            throw new InvalidDataException("An original object changed during extraction.");
        }

        return !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(original.Digest))
            ? throw new InvalidDataException("An original object failed integrity verification.")
            : bytes;
    }

    internal async Task<byte[]> ReadVerifiedPrefixAsync(OriginalObject original, int prefixBytes, long maxBytes, CancellationToken cancellationToken)
    {
        if (prefixBytes is < 1 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixBytes));
        }

        if (original.Length < 0 || original.Length > maxBytes)
        {
            throw new InvalidDataException("The original exceeds the media inspection byte budget.");
        }

        byte[] prefix = new byte[(int)Math.Min(original.Length, prefixBytes)];
        await CopyVerifiedAsync(original, Stream.Null, cancellationToken, prefix);
        return prefix;
    }

    internal async Task CopyVerifiedAsync(OriginalObject original, Stream destination, CancellationToken cancellationToken, Memory<byte> prefix = default)
    {
        ValidateDigest(original.Digest);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || original.Length < 0)
        {
            throw new ArgumentException("The original requires a writable export stream.");
        }

        await using FileStream input = OpenOriginal(original);
        await CopyVerifiedAsync(input, original, destination, prefix, cancellationToken);
    }

    private static async Task CopyVerifiedAsync(FileStream input, OriginalObject original, Stream destination, Memory<byte> prefix, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferLength);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0L;
            while (copied < original.Length)
            {
                int amount = (int)Math.Min(BufferLength, original.Length - copied);
                int read = await input.ReadAsync(buffer.AsMemory(0, amount), cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException("An original object ended before its recorded length.");
                }

                hash.AppendData(buffer, 0, read);
                if (copied < prefix.Length)
                {
                    int length = (int)Math.Min(read, prefix.Length - copied);
                    buffer.AsMemory(0, length).CopyTo(prefix.Slice((int)copied, length));
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
            }

            if (input.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(original.Digest)))
            {
                throw new InvalidDataException("An original object failed integrity verification.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal async Task CopyVerifiedToStoreAsync(OriginalObject original, OriginalFileStore destination, CancellationToken cancellationToken)
    {
        ValidateDigest(original.Digest);
        if (original.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(original));
        }

        await using FileStream input = OpenOriginal(original);
        string objects = EnsureDirectory(Path.Combine(destination.dataDirectory, "objects"));
        string first = EnsureDirectory(Path.Combine(objects, original.Digest[..2]));
        string second = EnsureDirectory(Path.Combine(first, original.Digest[2..4]));
        await using FileStream output = CreatePrivateFile(Path.Combine(second, original.Digest));
        await CopyVerifiedAsync(input, original, output, default, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private FileStream OpenOriginal(OriginalObject original)
    {
        FileStream input = OpenRead(ExistingObjectPath(original.Digest));
        try
        {
            return input.Length != original.Length
                ? throw new InvalidDataException("An original object has a conflicting length.")
                : input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static FileStream OpenRead(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            ? throw new InvalidDataException("An original object path is a symbolic link.")
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static FileStream CreatePrivateFile(string path)
    {
        FileStreamOptions options = new()
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

        return new FileStream(path, options);
    }

    internal async Task<string> MaterializeAsync(OriginalObject original, string extension, CancellationToken cancellationToken)
    {
        if (extension.Length is < 2 or > 9 || extension[0] != '.' || !extension[1..].All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The extension is invalid.", nameof(extension));
        }

        await VerifyAsync(original, cancellationToken);
        string root = Path.Combine(Path.GetTempPath(), "praxis-media");
        Directory.CreateDirectory(root);
        PruneMaterialized(root);
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, PrivateDirectoryMode);
        }

        // Objects are immutable once published, so a link to the verified object stays exact
        // without copying gigabytes of video.
        string path = Path.Combine(directory, "original" + extension.ToLowerInvariant());
        File.CreateSymbolicLink(path, ExistingObjectPath(original.Digest));
        return path;
    }

    internal static void ReleaseMaterialized(string path)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "praxis-media"));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || !string.Equals(Path.GetDirectoryName(directory), root, StringComparison.Ordinal))
        {
            throw new ArgumentException("The path was not materialized by the original store.", nameof(path));
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Pruned later.
        }
        catch (UnauthorizedAccessException)
        {
            // Pruned later.
        }
    }

    private static void PruneMaterialized(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-1))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Another worker may be using or removing it.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours.
            }
        }
    }

    private string ExistingObjectPath(string digest)
    {
        string objects = CheckDirectory(Path.Combine(dataDirectory, "objects"));
        string first = CheckDirectory(Path.Combine(objects, digest[..2]));
        string second = CheckDirectory(Path.Combine(first, digest[2..4]));
        return Path.Combine(second, digest);
    }

    public IEnumerable<string> EnumeratePublishedDigests()
    {
        string root = Path.Combine(dataDirectory, "objects");
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            yield break;
        }

        CheckDirectory(root);
        foreach (string first in Directory.EnumerateFileSystemEntries(root))
        {
            CheckDirectory(first);
            foreach (string second in Directory.EnumerateFileSystemEntries(first))
            {
                CheckDirectory(second);
                foreach (string file in Directory.EnumerateFileSystemEntries(second))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || !File.Exists(file))
                    {
                        throw new InvalidDataException("The object inventory contains an unexpected file.");
                    }

                    string digest = Path.GetFileName(file);
                    ValidateDigest(digest);
                    if (digest[..2] != Path.GetFileName(first) || digest[2..4] != Path.GetFileName(second))
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
        string staging = Path.Combine(dataDirectory, "staging");
        if (!Directory.Exists(staging) && !File.Exists(staging))
        {
            return 0;
        }

        CheckDirectory(staging);
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(staging))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || !File.Exists(entry) || Path.GetExtension(entry) != ".part")
            {
                throw new InvalidDataException("The staging inventory contains an unexpected entry.");
            }

            count = checked(count + 1);
        }

        return count;
    }

    private static async Task VerifyAsync(string path, string digest, long length, CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenRead(path);
        if (stream.Length != length || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, cancellationToken), Convert.FromHexString(digest)))
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

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~PrivateDirectoryMode) != 0)
        {
            throw new UnauthorizedAccessException("Original storage directories must be private.");
        }

        return path;
    }

    internal static bool IsValidDigest(string? digest)
        => digest is { Length: 64 } && digest.All(c
            => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateDigest(string digest)
    {
        if (!IsValidDigest(digest))
        {
            throw new ArgumentException("An original digest must be lowercase SHA-256.", nameof(digest));
        }
    }
}
