using System.Buffers.Binary;

namespace Praxis.Storage;

internal static class LocalMediaHeaderParser
{
    internal static (string? Container, long? DurationMilliseconds) Read(
        ReadOnlySpan<byte> header, string extension)
    {
        if (extension is ".webm" or ".mkv")
        {
            return HasSupportedEbmlHeader(header, extension)
                ? ("ebml", null) : (null, null);
        }

        if (extension is not (".mp4" or ".mov" or ".m4v") ||
            header.Length < 16 || !header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return (null, null);
        }

        var ftypLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (ftypLength < 16 || ftypLength > header.Length ||
            (ftypLength - 16) % 4 != 0)
        {
            return (null, null);
        }

        for (var i = 8; i < 12; i++)
        {
            if (header[i] is < 0x20 or > 0x7e)
            {
                return (null, null);
            }
        }

        return ("iso-bmff", ReadMovieDuration(header, (int)ftypLength));
    }

    private static bool HasSupportedEbmlHeader(
        ReadOnlySpan<byte> header, string extension)
    {
        if (header.Length < 12 || header[0] != 0x1a || header[1] != 0x45 ||
            header[2] != 0xdf || header[3] != 0xa3 ||
            (header[4] & 0x80) == 0)
        {
            return false;
        }

        var size = header[4] & 0x7f;
        if (size < 7 || size == 0x7f || size > header.Length - 5)
        {
            return false;
        }

        var body = header.Slice(5, size);
        for (var i = 0; i <= body.Length - 3; i++)
        {
            if (body[i] != 0x42 || body[i + 1] != 0x82 ||
                (body[i + 2] & 0x80) == 0)
            {
                continue;
            }

            var length = body[i + 2] & 0x7f;
            if (i + 3 + length > body.Length)
            {
                return false;
            }

            var type = body.Slice(i + 3, length);
            return type.SequenceEqual("webm"u8) ||
                (extension == ".mkv" && type.SequenceEqual("matroska"u8));
        }

        return false;
    }

    private static long? ReadMovieDuration(ReadOnlySpan<byte> header, int offset)
    {
        while (offset <= header.Length - 8)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(offset, 4));
            if (length < 8 || length > header.Length - offset)
            {
                return null;
            }

            if (header.Slice(offset + 4, 4).SequenceEqual("moov"u8))
            {
                return ReadMovieHeader(header.Slice(offset + 8, (int)length - 8));
            }

            offset += (int)length;
        }

        return null;
    }

    private static long? ReadMovieHeader(ReadOnlySpan<byte> moov)
    {
        var offset = 0;
        while (offset <= moov.Length - 8)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(moov.Slice(offset, 4));
            if (length < 8 || length > moov.Length - offset)
            {
                return null;
            }

            if (moov.Slice(offset + 4, 4).SequenceEqual("mvhd"u8))
            {
                var body = moov.Slice(offset + 8, (int)length - 8);
                if (body.Length < 20)
                {
                    return null;
                }

                long duration;
                uint timescale;
                if (body[0] == 0)
                {
                    timescale = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12, 4));
                    var rawDuration = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(16, 4));
                    if (rawDuration == uint.MaxValue)
                    {
                        return null;
                    }

                    duration = rawDuration;
                }
                else if (body[0] == 1 && body.Length >= 32)
                {
                    timescale = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(20, 4));
                    var rawDuration = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(24, 8));
                    if (rawDuration == ulong.MaxValue || rawDuration > long.MaxValue)
                    {
                        return null;
                    }

                    duration = (long)rawDuration;
                }
                else
                {
                    return null;
                }

                if (timescale == 0 || duration <= 0)
                {
                    return null;
                }

                var milliseconds = duration * 1000m / timescale;
                return milliseconds <= 86_400_000m
                    ? (long)decimal.Round(milliseconds, 0, MidpointRounding.AwayFromZero)
                    : null;
            }

            offset += (int)length;
        }

        return null;
    }
}
