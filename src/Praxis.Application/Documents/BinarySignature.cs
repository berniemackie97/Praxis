namespace Praxis.Application.Documents;

/// <summary>Content signatures of binary formats, so a binary file is never decoded as text.</summary>
public static class BinarySignature
{
    private static readonly byte[][] Signatures =
    [
        "%PDF-"u8.ToArray(), [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], [0x50, 0x4b, 0x03, 0x04], [0x7f, 0x45, 0x4c, 0x46],
        [0x1f, 0x8b], [0xcf, 0xfa, 0xed, 0xfe], [0xfe, 0xed, 0xfa, 0xcf], [0xca, 0xfe, 0xba, 0xbe], [0xff, 0xd8, 0xff],
        "GIF8"u8.ToArray(), [0x49, 0x49, 0x2a, 0x00], [0x4d, 0x4d, 0x00, 0x2a], "BM"u8.ToArray(), [0xd0, 0xcf, 0x11, 0xe0],
        "Rar!"u8.ToArray(), [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c], "MZ"u8.ToArray()
    ];

    /// <summary>
    /// Extensions of binary formats. A file carrying one is never decoded as text, whatever its
    /// bytes look like: it is read by its format's reader or not at all.
    /// </summary>
    public static readonly IReadOnlySet<string> BinaryExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".webp", ".ico",
        ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".mp3", ".m4a", ".wav", ".aac", ".flac", ".ogg", ".opus",
        ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".dmg", ".iso", ".pkg", ".exe", ".dll", ".so", ".dylib", ".bin", ".o", ".a",
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx", ".pptx", ".key", ".pages", ".numbers", ".sqlite", ".db", ".class", ".jar"
    };

    public static bool Matches(ReadOnlySpan<byte> bytes)
    {
        foreach (byte[] signature in Signatures)
        {
            if (bytes.StartsWith(signature))
            {
                return true;
            }
        }

        // ISO base media (MP4, MOV, HEIC) carries "ftyp" at offset 4.
        return bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8);
    }
}
