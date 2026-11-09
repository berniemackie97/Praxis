namespace Praxis.Storage.RecoveryCheck;

internal sealed class GeneratedContentStream(long length) : Stream
{
    private static readonly byte[] Pattern = CreatePattern();
    private long position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(length - position, buffer.Length);
        var remaining = count;
        while (remaining > 0)
        {
            var offset = (int)(position % Pattern.Length);
            var chunk = Math.Min(remaining, Pattern.Length - offset);
            Pattern.AsSpan(offset, chunk).CopyTo(buffer);
            buffer = buffer[chunk..];
            remaining -= chunk;
            position += chunk;
        }

        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    private static byte[] CreatePattern()
    {
        var result = new byte[64 * 1024];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((i * 37 + (i >> 8)) & 255);
        }

        return result;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
