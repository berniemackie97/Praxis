namespace Praxis.Application.Understanding;

/// <summary>
/// Splits long text into spans that fit one strong-tier call, cutting at page breaks, then blank
/// lines, then line ends near the limit so no evidence quote is split. Text beyond the last span
/// is reported as not understood rather than silently dropped.
/// </summary>
public static class DocumentChunker
{
    public const int MaximumChunkCharacters = 60_000;
    public const int MaximumChunks = 12;

    public static (IReadOnlyList<DocumentChunk> Chunks, bool Complete) Split(string text, int maximumCharacters = MaximumChunkCharacters, int maximumChunks = MaximumChunks)
    {
        List<DocumentChunk> chunks = [];
        int offset = 0;
        while (offset < text.Length && chunks.Count < maximumChunks)
        {
            int remaining = text.Length - offset;
            if (remaining <= maximumCharacters)
            {
                chunks.Add(new DocumentChunk(chunks.Count, offset, remaining));
                offset = text.Length;
                break;
            }

            int floor = offset + maximumCharacters / 2;
            int limit = offset + maximumCharacters;
            int cut = Last(text, "\n\f\n", floor, limit) ?? Last(text, "\n\n", floor, limit) ?? Last(text, "\n", floor, limit) ?? limit;
            chunks.Add(new DocumentChunk(chunks.Count, offset, cut - offset));
            offset = cut;
        }

        return (chunks, offset >= text.Length);
    }

    private static int? Last(string text, string separator, int floor, int limit)
    {
        int index = text.LastIndexOf(separator, limit - 1, limit - floor, StringComparison.Ordinal);
        return index < 0 || index + separator.Length > limit ? null : index + separator.Length;
    }
}
