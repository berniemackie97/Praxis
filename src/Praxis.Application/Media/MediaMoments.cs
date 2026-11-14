using Praxis.Application.Text;
using Praxis.Domain.Media;
using Praxis.Domain.Search;
using Praxis.Domain.Text;

namespace Praxis.Application.Media;

/// <summary>
/// Turns retrieval hits on media into moments: the few seconds to minutes where the matching words
/// are spoken. Works in code over the stored transcript timing (or a caption file's cues), so a
/// clip costs no tokens and its bounds are exact.
/// </summary>
public sealed class MediaMoments(ITextDerivativeRepository text, MediaEvidence? captions = null)
{
    public const long LeadInMilliseconds = 1_500;
    public const long TailMilliseconds = 1_000;
    public const long MinimumMilliseconds = 8_000;
    public const long MaximumMilliseconds = 180_000;
    private const int WindowSpans = 4;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "where", "when", "what", "how", "they", "them", "their", "there", "about", "show",
        "part", "clip", "video", "find", "does", "did", "from", "into", "talk", "talks", "talking", "says", "said", "explain", "explains"
    };

    /// <summary>True when the hit's source is media this class can cut moments from.</summary>
    public bool IsMedia(SearchHit hit) => Timeline(hit.ItemId, hit.Revision, hit.SourceLocator, hit.SourceId) is not null;

    /// <summary>The best non-overlapping moments for <paramref name="query"/> in one source, most relevant first.</summary>
    public IReadOnlyList<MediaMoment> Locate(SearchHit hit, string query, int maximum)
    {
        if (Timeline(hit.ItemId, hit.Revision, hit.SourceLocator, hit.SourceId) is not { } timeline)
        {
            return [];
        }

        string[] terms = Terms(query);
        int? anchor = SnippetAnchor(timeline, hit.Snippet);
        List<(int First, int Last, double Score)> windows = [];
        for (int first = 0; first < timeline.Spans.Count; first++)
        {
            for (int last = first; last < Math.Min(timeline.Spans.Count, first + WindowSpans); last++)
            {
                double score = Score(timeline, first, last, terms);
                if (score > 0)
                {
                    // Prefer the tightest window that reaches a score.
                    windows.Add((first, last, score - (0.01 * (last - first))));
                }
            }
        }

        // When no words match (a semantic hit), the snippet marks where the passage is spoken.
        if (windows.Count == 0 && anchor is { } index)
        {
            windows.Add((index, index, 1));
        }

        List<MediaMoment> moments = [];
        foreach ((int first, int last, double _) in windows.OrderByDescending(window => window.Score))
        {
            MediaMoment moment = Build(hit.SourceId, hit.ItemId, hit.Revision, hit.SourceLocator, timeline, first, last);
            if (moments.Any(existing => existing.StartMilliseconds < moment.EndMilliseconds && moment.StartMilliseconds < existing.EndMilliseconds))
            {
                continue;
            }

            moments.Add(moment);
            if (moments.Count == maximum)
            {
                break;
            }
        }

        return moments;
    }

    /// <summary>The whole media as one moment, for "show me the full video".</summary>
    public MediaMoment? Whole(Guid sourceId, Guid itemId, int revision, string locator)
    {
        if (Timeline(itemId, revision, locator, sourceId) is not { } timeline || timeline.Spans.Count == 0)
        {
            return null;
        }

        TimedSpan last = timeline.Spans[^1];
        string excerpt = timeline.Content.Length <= 600 ? timeline.Content : timeline.Content[..600] + "…";
        return new MediaMoment(sourceId, itemId, revision, Path.GetFileName(locator), timeline.MediaSourceId ?? sourceId, 0,
            last.EndMilliseconds + TailMilliseconds, excerpt, 0, Math.Min(timeline.Content.Length, 600), WholeMedia: true);
    }

    /// <summary>The character offset spoken at <paramref name="milliseconds"/> in a source's transcript, if it has timing.</summary>
    public int? OffsetAt(Guid sourceId, Guid itemId, int revision, string locator, long milliseconds)
    {
        if (Timeline(itemId, revision, locator, sourceId) is not { } timeline || timeline.Spans.Count == 0)
        {
            return null;
        }

        TimedSpan span = timeline.Spans.LastOrDefault(span => span.StartMilliseconds <= milliseconds) ?? timeline.Spans[0];
        return span.Offset;
    }

    private static MediaMoment Build(Guid sourceId, Guid itemId, int revision, string locator, MediaTimeline timeline, int first, int last)
    {
        TimedSpan from = timeline.Spans[first];
        TimedSpan to = timeline.Spans[last];
        long start = Math.Max(0, from.StartMilliseconds - LeadInMilliseconds);
        // Keep a watchable minimum by playing on past the words, and cap the length.
        long end = Math.Min(start + MaximumMilliseconds, Math.Max(to.EndMilliseconds + TailMilliseconds, start + MinimumMilliseconds));
        int offset = from.Offset;
        int length = Math.Min(to.Offset + to.Length, timeline.Content.Length) - offset;
        return new MediaMoment(sourceId, itemId, revision, Path.GetFileName(locator), timeline.MediaSourceId ?? sourceId, start, end,
            timeline.Content.Substring(offset, length), offset, length, WholeMedia: false);
    }

    private static double Score(MediaTimeline timeline, int first, int last, string[] terms)
    {
        if (terms.Length == 0)
        {
            return 0;
        }

        int start = timeline.Spans[first].Offset;
        int end = Math.Min(timeline.Content.Length, timeline.Spans[last].Offset + timeline.Spans[last].Length);
        string window = timeline.Content[start..end];
        int matched = terms.Count(term => ContainsWordPrefix(window, term));
        return matched == 0 ? 0 : matched / (double)terms.Length;
    }

    private static bool ContainsWordPrefix(string text, string term)
    {
        // Match a word that starts with the term's stem, so "configure" finds "configuring".
        string stem = term.Length > 5 ? term[..^Math.Min(3, term.Length - 5)] : term;
        int index = 0;
        while ((index = text.IndexOf(stem, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
            {
                return true;
            }

            index += stem.Length;
        }

        return false;
    }

    private static string[] Terms(string query) => new string(query.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(term => term.Length >= 3 && !StopWords.Contains(term))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(12)
        .ToArray();

    /// <summary>The span holding the longest fragment of the search snippet, which marks where semantic search matched.</summary>
    private static int? SnippetAnchor(MediaTimeline timeline, string snippet)
    {
        string? fragment = snippet.Split(['…', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Replace("<b>", string.Empty, StringComparison.Ordinal).Replace("</b>", string.Empty, StringComparison.Ordinal))
            .Where(part => part.Length >= 24)
            .MaxBy(part => part.Length);
        if (fragment is null)
        {
            return null;
        }

        int offset = timeline.Content.IndexOf(fragment.Length > 80 ? fragment[..80] : fragment, StringComparison.OrdinalIgnoreCase);
        if (offset < 0)
        {
            return null;
        }

        int index = timeline.Spans.ToList().FindLastIndex(span => span.Offset <= offset);
        return index < 0 ? null : index;
    }

    private MediaTimeline? Timeline(Guid itemId, int revision, string locator, Guid sourceId)
    {
        TextDerivative? derivative = text.ReadCurrent(itemId, revision);
        if (derivative is not { Outcome: "completed", Content: { Length: > 0 } content } || derivative.SourceId != sourceId)
        {
            return null;
        }

        IReadOnlyList<TimedSpan> timings = text.ReadTimings(itemId, revision);
        if (timings.Count > 0)
        {
            return new MediaTimeline(content, timings, null);
        }

        string extension = Path.GetExtension(locator).ToLowerInvariant();
        if (extension is not (".vtt" or ".srt") || captions?.PairedVideo(locator) is not { } video)
        {
            return null;
        }

        try
        {
            TimedSpan[] cues = TimedCaptionParser.Parse(content, extension)
                .Select(cue => new TimedSpan(cue.Offset, Math.Max(1, cue.Length), cue.StartMilliseconds, cue.EndMilliseconds))
                .ToArray();
            return cues.Length == 0 ? null : new MediaTimeline(content, cues, video.Source.SourceId);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private sealed record MediaTimeline(string Content, IReadOnlyList<TimedSpan> Spans, Guid? MediaSourceId);
}
