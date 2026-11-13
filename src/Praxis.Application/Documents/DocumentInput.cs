namespace Praxis.Application.Documents;

/// <summary>
/// A verified original handed to a reader: its file name (for extension hints), bytes, and a
/// stable key derived from the source for structures that need a fallback identity.
/// </summary>
public sealed record DocumentInput(string FileName, byte[] Bytes, string SourceKey)
{
    public string Extension { get; } = Path.GetExtension(FileName).ToLowerInvariant();

    public bool StartsWith(ReadOnlySpan<byte> signature) => Bytes.AsSpan().StartsWith(signature);
}
