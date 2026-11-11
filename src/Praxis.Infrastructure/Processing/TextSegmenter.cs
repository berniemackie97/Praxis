using Praxis.Domain.Text;

namespace Praxis.Infrastructure.Processing;

/// <summary>
/// Deterministic, structure-aware segmentation of extracted text. Blocks start at Markdown
/// headings (outside fenced code) and after blank lines; blocks are packed into segments of
/// roughly <see cref="TargetCharacters"/> without exceeding <see cref="MaximumCharacters"/>,
/// splitting oversized blocks at line boundaries and, as a last resort, at the limit.
/// Segments are contiguous and cover the whole text exactly. Text without any
/// non-whitespace character produces no segments.
/// </summary>
internal static class TextSegmenter
{
    internal const int MinimumCharacters = 200;
    internal const int TargetCharacters = 1_200;
    internal const int MaximumCharacters = 2_000;
    internal const int MaximumSegments = 4_096;
    private const int MaximumHeadingPath = 512;

    private readonly record struct Block(int Start, int End, string? HeadingPath, bool StartsHeading);

    internal static IReadOnlyList<TextSegmentSpan> Segment(string text, bool markdown)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<TextSegmentSpan> segments = new List<TextSegmentSpan>();
        int start = -1;
        int end = 0;
        string? path = null;
        foreach (Block block in Blocks(text, markdown))
        {
            int length = end - start;
            bool breakAtHeading = block.StartsHeading && start >= 0 && length >= MinimumCharacters;
            bool full = start >= 0 && (length >= TargetCharacters ||
                                       length + (block.End - block.Start) > MaximumCharacters);
            if (breakAtHeading || full)
            {
                Emit(text, start, end, path, segments);
                start = -1;
            }

            if (start < 0)
            {
                start = block.Start;
                path = block.HeadingPath;
            }

            end = block.End;
        }

        Emit(text, start, end, path, segments);
        return segments;
    }

    private static IEnumerable<Block> Blocks(string text, bool markdown)
    {
        List<(int Level, string Title)> headings = new List<(int Level, string Title)>();
        bool fenced = false;
        int blockStart = 0;
        bool blockStartsHeading = false;
        string? blockPath = null;
        bool previousBlank = false;
        for (int offset = 0; offset < text.Length;)
        {
            int newline = text.IndexOf('\n', offset);
            int lineEnd = newline < 0 ? text.Length : newline + 1;
            ReadOnlySpan<char> line = text.AsSpan(offset, lineEnd - offset).TrimEnd("\r\n");
            bool blank = line.IsWhiteSpace();
            bool insideFence = fenced;
            (int Level, string Title)? heading = markdown && !insideFence ? HeadingOf(line) : null;
            if (markdown && IsFence(line))
            {
                fenced = !fenced;
            }

            // Blank lines inside fenced code do not split it; an opening fence can start a block.
            bool boundary = offset > 0 &&
                            (heading is not null || (previousBlank && !blank && !insideFence));
            if (boundary)
            {
                yield return new Block(blockStart, offset, blockPath, blockStartsHeading);
                blockStart = offset;
                blockStartsHeading = heading is not null;
            }
            else if (offset == 0)
            {
                blockStartsHeading = heading is not null;
            }

            if (heading is { } found)
            {
                headings.RemoveAll(entry => entry.Level >= found.Level);
                headings.Add(found);
            }

            if (boundary || offset == 0)
            {
                blockPath = PathOf(headings);
            }

            previousBlank = blank;
            offset = lineEnd;
        }

        yield return new Block(blockStart, text.Length, blockPath, blockStartsHeading);
    }

    private static void Emit(
        string text, int start, int end, string? path, List<TextSegmentSpan> segments)
    {
        if (start < 0)
        {
            return;
        }

        // Oversized ranges split at the last line break that fits, else at the limit, never
        // between the halves of a surrogate pair.
        while (end - start > MaximumCharacters)
        {
            int limit = start + MaximumCharacters;
            int lineBreak = text.LastIndexOf('\n', limit - 1, MaximumCharacters - 1);
            int split = lineBreak > start ? lineBreak + 1 : limit;
            if (char.IsLowSurrogate(text[split]))
            {
                split--;
            }

            Add(segments, new TextSegmentSpan(start, split - start, path));
            start = split;
        }

        Add(segments, new TextSegmentSpan(start, end - start, path));
    }

    private static void Add(List<TextSegmentSpan> segments, TextSegmentSpan segment)
    {
        if (segments.Count == MaximumSegments)
        {
            throw new InvalidDataException("The text exceeds the supported segment count.");
        }

        segments.Add(segment);
    }

    private static (int Level, string Title)? HeadingOf(ReadOnlySpan<char> line)
    {
        int indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3)
        {
            return null;
        }

        ReadOnlySpan<char> rest = line[indent..];
        int level = 0;
        while (level < rest.Length && level < 6 && rest[level] == '#')
        {
            level++;
        }

        if (level == 0 || (level < rest.Length && rest[level] is not (' ' or '\t')))
        {
            return null;
        }

        ReadOnlySpan<char> title = rest[level..].Trim().TrimEnd('#').Trim();
        return title.IsEmpty ? null : (level, title.ToString());
    }

    private static bool IsFence(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> trimmed = line.TrimStart(' ');
        return line.Length - trimmed.Length <= 3 &&
            (trimmed.StartsWith("```", StringComparison.Ordinal) ||
             trimmed.StartsWith("~~~", StringComparison.Ordinal));
    }

    private static string? PathOf(List<(int Level, string Title)> headings)
    {
        if (headings.Count == 0)
        {
            return null;
        }

        string path = string.Join(" > ", headings.Select(heading => heading.Title));
        if (path.Length <= MaximumHeadingPath)
        {
            return path;
        }

        int cut = char.IsHighSurrogate(path[MaximumHeadingPath - 1])
            ? MaximumHeadingPath - 1 : MaximumHeadingPath;
        return path[..cut];
    }
}
