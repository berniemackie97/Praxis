using System.Text;
using Praxis.Domain.Media;

namespace Praxis.Application.Media;

/// <summary>
/// Turns recognized segments into the text the read stage stores (readable prose, a paragraph per
/// pause, so quotes, search and models see natural language) and the timing of every utterance as
/// character spans, so any character of the transcript maps back to its moment in the media.
/// </summary>
public static class TranscriptText
{
    private const long ParagraphPauseMilliseconds = 2_000;
    private const int ParagraphCharacters = 900;

    public static (string Text, IReadOnlyList<TimedSpan> Timings) Compose(IEnumerable<TranscriptSegment> segments)
    {
        StringBuilder builder = new();
        List<TimedSpan> timings = [];
        long previousEnd = 0;
        int paragraphStart = 0;
        foreach (TranscriptSegment segment in segments.OrderBy(segment => segment.StartMilliseconds))
        {
            string text = Clean(segment.Text);
            if (text.Length == 0 || timings.Count == TimedSpan.MaximumSpans)
            {
                continue;
            }

            long start = Math.Max(segment.StartMilliseconds, previousEnd);
            long end = Math.Max(segment.EndMilliseconds, start + 1);
            if (builder.Length > 0)
            {
                bool paragraph = start - previousEnd >= ParagraphPauseMilliseconds || builder.Length - paragraphStart >= ParagraphCharacters;
                builder.Append(paragraph ? "\n\n" : " ");
                if (paragraph)
                {
                    paragraphStart = builder.Length;
                }
            }

            timings.Add(new TimedSpan(builder.Length, text.Length, start, end));
            builder.Append(text);
            previousEnd = end;
        }

        return (builder.ToString(), timings);
    }

    private static string Clean(string text)
    {
        StringBuilder builder = new(text.Length);
        bool space = false;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
