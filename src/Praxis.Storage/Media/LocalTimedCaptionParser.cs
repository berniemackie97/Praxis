namespace Praxis.Storage;

internal sealed record TimedCue(
    int Number, long StartMilliseconds, long EndMilliseconds,
    int Offset, int Length, string Text);

internal static class LocalTimedCaptionParser
{
    private const int MaximumCues = 2_000;
    private const int MaximumCueCharacters = 700;
    private const long MaximumCueMilliseconds = 120_000;
    private const long MaximumMediaMilliseconds = 86_400_000;

    internal static IReadOnlyList<TimedCue> Parse(string content, string extension)
    {
        var lines = ReadLines(content);
        if (extension == ".vtt" &&
            (lines.Count == 0 || !lines[0].Text.StartsWith("WEBVTT", StringComparison.Ordinal) ||
             (lines[0].Text.Length > 6 && !char.IsWhiteSpace(lines[0].Text[6]))))
        {
            throw new InvalidDataException("The WebVTT signature is invalid.");
        }

        var startIndex = extension == ".vtt" ? 1 : 0;
        if (extension == ".vtt")
        {
            while (startIndex < lines.Count && lines[startIndex].Text.Length > 0)
            {
                startIndex++;
            }
        }

        var cues = new List<TimedCue>();
        var previousEnd = -1L;
        while (startIndex < lines.Count)
        {
            while (startIndex < lines.Count && string.IsNullOrWhiteSpace(lines[startIndex].Text))
            {
                startIndex++;
            }

            if (startIndex == lines.Count)
            {
                break;
            }

            var blockStart = startIndex;
            while (startIndex < lines.Count && !string.IsNullOrWhiteSpace(lines[startIndex].Text))
            {
                startIndex++;
            }

            var timingIndex = blockStart;
            if (!lines[timingIndex].Text.Contains("-->", StringComparison.Ordinal))
            {
                timingIndex++;
            }

            if (timingIndex >= startIndex ||
                !TryTiming(lines[timingIndex].Text, extension, out var from, out var to) ||
                from < previousEnd || to <= from ||
                to - from > MaximumCueMilliseconds || to > MaximumMediaMilliseconds ||
                timingIndex + 1 == startIndex)
            {
                throw new InvalidDataException("A timed-caption cue has invalid timing or text.");
            }

            var textOffset = lines[timingIndex + 1].Offset;
            var end = lines[startIndex - 1].Offset + lines[startIndex - 1].Text.Length;
            var length = end - textOffset;
            if (length is < 1 or > MaximumCueCharacters)
            {
                throw new InvalidDataException("A timed-caption cue exceeds the text limit.");
            }

            var text = string.Join(" ", lines.Skip(timingIndex + 1)
                .Take(startIndex - timingIndex - 1).Select(line => line.Text.Trim()));
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("A timed-caption cue must contain text.");
            }

            cues.Add(new TimedCue(cues.Count + 1, from, to, textOffset, length, text));
            if (cues.Count > MaximumCues)
            {
                throw new InvalidDataException("The timed-caption file exceeds its cue budget.");
            }

            previousEnd = to;
        }

        return cues;
    }

    private static bool TryTiming(
        string text, string extension, out long start, out long end)
    {
        start = end = 0;
        var parts = text.Split("-->", StringSplitOptions.None);
        return parts.Length == 2 &&
            TryTimestamp(parts[0].Trim(), extension == ".srt" ? ',' : '.', out start) &&
            TryTimestamp(parts[1].Trim(), extension == ".srt" ? ',' : '.', out end);
    }

    private static bool TryTimestamp(string text, char separator, out long milliseconds)
    {
        milliseconds = 0;
        var fragments = text.Split(separator);
        if (fragments.Length != 2 || fragments[1].Length != 3 ||
            !int.TryParse(fragments[1], out var ms) || ms is < 0 or > 999)
        {
            return false;
        }

        var fields = fragments[0].Split(':');
        if (fields.Length is not (2 or 3) ||
            fields.Any(field => field.Length != 2 ||
                !int.TryParse(field, out _)))
        {
            return false;
        }

        var hours = fields.Length == 3 ? int.Parse(fields[0]) : 0;
        var minutes = int.Parse(fields[^2]);
        var seconds = int.Parse(fields[^1]);
        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            return false;
        }

        milliseconds = (hours * 3600L + minutes * 60L + seconds) * 1000 + ms;
        return true;
    }

    private static IReadOnlyList<(int Offset, string Text)> ReadLines(string text)
    {
        var lines = new List<(int, string)>();
        for (var offset = 0; offset < text.Length;)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0)
            {
                next = text.Length;
            }

            var end = next > offset && text[next - 1] == '\r' ? next - 1 : next;
            lines.Add((offset, text[offset..end]));
            if (lines.Count > 5_000)
            {
                throw new InvalidDataException("The timed-caption file exceeds its line budget.");
            }
            offset = next == text.Length ? text.Length : next + 1;
        }

        return lines;
    }
}
