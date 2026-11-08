using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Praxis.Knowledge;

public sealed record AttachmentTransferStatus(string Sha256, int Length, int Uploaded);

public sealed class LocalAttachmentTransferStore
{
    public const int MaxAttachmentBytes = 32 * 1024 * 1024;
    public const int MaxChunkBytes = 1024 * 1024;

    private const int HeaderLength = 40;
    private readonly string stagingDirectory;
    private readonly string contentDirectory;

    public LocalAttachmentTransferStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.GetFullPath(directory);
        stagingDirectory = Path.Combine(root, "staging");
        contentDirectory = Path.Combine(root, "content");
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(contentDirectory);
    }

    public AttachmentTransferStatus Begin(Guid transferId, string sha256, int length)
    {
        ValidateId(transferId);
        ValidateDigest(sha256);
        ValidateLength(length);

        using var stream = new FileStream(StagingPath(transferId),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length == 0)
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            "PTF1"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..8], length);
            Convert.FromHexString(sha256).CopyTo(header[8..]);
            stream.Write(header);
            stream.Flush(flushToDisk: true);
        }

        var status = ReadStatus(stream);
        if (status.Sha256 != sha256 || status.Length != length)
        {
            throw new InvalidOperationException("The transfer identity is assigned to different content.");
        }

        return status;
    }

    public AttachmentTransferStatus Status(Guid transferId)
    {
        using var stream = OpenStaging(transferId);
        return ReadStatus(stream);
    }

    public int Append(Guid transferId, int offset, ReadOnlySpan<byte> chunk)
    {
        if (offset < 0 || chunk.IsEmpty || chunk.Length > MaxChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk), "A transfer chunk must contain 1 to 1048576 bytes at a nonnegative offset.");
        }

        using var stream = OpenStaging(transferId);
        var status = ReadStatus(stream);
        if (offset > status.Uploaded || (long)offset + chunk.Length > status.Length)
        {
            throw new InvalidOperationException("The chunk is outside the expected upload range.");
        }

        if (offset < status.Uploaded)
        {
            if (offset + (long)chunk.Length > status.Uploaded)
            {
                throw new InvalidOperationException("A partially overlapping retry must resume from the current offset.");
            }

            var previous = new byte[chunk.Length];
            stream.Position = HeaderLength + offset;
            stream.ReadExactly(previous);
            if (!chunk.SequenceEqual(previous))
            {
                throw new InvalidDataException("The retried chunk differs from the staged bytes.");
            }

            return status.Uploaded;
        }

        stream.Position = HeaderLength + offset;
        stream.Write(chunk);
        stream.Flush(flushToDisk: true);
        return status.Uploaded + chunk.Length;
    }

    public string Commit(Guid transferId)
    {
        var stagingPath = StagingPath(transferId);
        string digest;
        using (var staging = OpenStaging(transferId))
        {
            var status = ReadStatus(staging);
            if (status.Uploaded != status.Length)
            {
                throw new InvalidOperationException("The transfer has not received all declared bytes.");
            }

            digest = status.Sha256;
            if (!IsCommitted(status.Sha256, status.Length))
            {
                var temporary = Path.Combine(contentDirectory, $"{Guid.NewGuid():N}.pending");
                try
                {
                    using (var destination = new FileStream(temporary,
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        staging.Position = HeaderLength;
                        staging.CopyTo(destination);
                        destination.Flush(flushToDisk: true);
                        destination.Position = 0;
                        if (destination.Length != status.Length ||
                            !CryptographicOperations.FixedTimeEquals(
                                SHA256.HashData(destination), Convert.FromHexString(digest)))
                        {
                            throw new InvalidDataException("The attachment content failed integrity verification.");
                        }
                    }

                    try
                    {
                        File.Move(temporary, ContentPath(digest));
                    }
                    catch (IOException) when (File.Exists(ContentPath(digest)))
                    {
                        if (!IsCommitted(digest, status.Length))
                        {
                            throw new InvalidDataException("The published attachment is invalid.");
                        }
                    }
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
        }

        File.Delete(stagingPath);
        return digest;
    }

    public bool IsCommitted(string sha256, int length)
    {
        ValidateDigest(sha256);
        ValidateLength(length);

        var path = ContentPath(sha256);
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != length ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(stream), Convert.FromHexString(sha256)))
        {
            throw new InvalidDataException("The stored attachment failed integrity verification.");
        }

        return true;
    }

    public byte[] ReadVerified(string sha256)
    {
        ValidateDigest(sha256);
        using var stream = new FileStream(ContentPath(sha256),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxAttachmentBytes)
        {
            throw new InvalidDataException("The stored attachment exceeds the size limit.");
        }

        var content = new byte[(int)stream.Length];
        stream.ReadExactly(content);
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(content), Convert.FromHexString(sha256)))
        {
            throw new InvalidDataException("The stored attachment failed integrity verification.");
        }

        return content;
    }

    private FileStream OpenStaging(Guid transferId)
    {
        ValidateId(transferId);
        return new FileStream(StagingPath(transferId),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static AttachmentTransferStatus ReadStatus(FileStream stream)
    {
        if (stream.Length < HeaderLength || stream.Length > HeaderLength + MaxAttachmentBytes)
        {
            throw new InvalidDataException("The transfer contains an invalid header or oversized payload.");
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("PTF1"u8))
        {
            throw new InvalidDataException("The transfer header is not recognized.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        if (length is < 0 or > MaxAttachmentBytes || stream.Length - HeaderLength > length)
        {
            throw new InvalidDataException("The transfer length is invalid.");
        }

        return new AttachmentTransferStatus(
            Convert.ToHexStringLower(header[8..]), length, (int)(stream.Length - HeaderLength));
    }

    private string StagingPath(Guid transferId) => Path.Combine(stagingDirectory, $"{transferId:N}.part");

    private string ContentPath(string sha256) => Path.Combine(contentDirectory, sha256);

    private static void ValidateId(Guid transferId)
    {
        if (transferId == Guid.Empty)
        {
            throw new ArgumentException("A transfer ID must be nonempty.", nameof(transferId));
        }
    }

    private static void ValidateLength(int length)
    {
        if (length is < 0 or > MaxAttachmentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }

    private static void ValidateDigest(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.Any(character =>
            character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The content digest must be lowercase SHA-256 hexadecimal.", nameof(sha256));
        }
    }
}
