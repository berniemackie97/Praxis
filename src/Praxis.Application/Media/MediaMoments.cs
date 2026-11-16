using Praxis.Application.Text;
using Praxis.Domain.Media;
using Praxis.Domain.Search;
using Praxis.Domain.Text;

namespace Praxis.Application.Media;

/// <summary>
/// Turns retrieval hits on media into moments: the passage, from half a minute to a few minutes, where
/// the matching topic is discussed. Works in code over the stored transcript timing (or a caption file's cues), so a
/// clip costs no tokens and its bounds are exact.
/// </summary>
public sealed class MediaMoments(ITextDerivativeRepository text, MediaEvidence? captions = null)
{
    public const long LeadInMilliseconds = 1_500;
    public const long TailMilliseconds = 1_000;
    public const long MinimumMilliseconds = 30_000;
    public const long MaximumMilliseconds = 180_000;

    // Mentions closer than this belong to one passage about the topic.
    private const long PassageGapMilliseconds = 25_000;
    // A passage longer than this is split so the densest part wins.
    private const long PassageMaximumMilliseconds = 120_000;
    // Silence longer than this ends a clip; the next words belong to something else.
    private const long SilenceMilliseconds = 8_000;
    // How far past the minimum a clip may run to finish the sentence it is in.
    private const long SentenceGraceMilliseconds = 15_000;
    private const int LeadSpans = 3;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "where", "when", "what", "how", "they", "them", "their", "there", "about", "show",
        "part", "clip", "video", "find", "does", "did", "from", "into", "talk", "talks", "talking", "says", "said", "explain", "explains",
        "works", "work", "working"
    };

    /// <summary>True when the hit's source is media this class can cut moments from.</summary>
    public bool IsMedia(SearchHit hit) => Timeline(hit.ItemId, hit.Revision, hit.SourceLocator, hit.SourceId) is not null;

    /// <summary>
    /// The best non-overlapping moments for <paramref name="query"/> in one source, most relevant first. A moment is the passage
    /// where the topic is discussed most (not merely its first mention), widened to whole sentences and a watchable length.
    /// Moments overlapping <paramref name="avoid"/> (already shown) are left out so a follow-up gets different clips.
    /// </summary>
    public IReadOnlyList<MediaMoment> Locate(SearchHit hit, string query, int maximum, bool exactTopic = false,
        IReadOnlyList<(long Start, long End)>? avoid = null)
    {
        if (Timeline(hit.ItemId, hit.Revision, hit.SourceLocator, hit.SourceId) is not { } timeline)
        {
            return [];
        }

        string[][] terms = Terms(query).Select(term => Variants(term, timeline.Content)).ToArray();
        List<Passage> passages = Passages(timeline, terms);

        // When no words match (a semantic hit), the snippet marks where the passage is spoken.
        if (passages.Count == 0 && !exactTopic && SnippetAnchor(timeline, hit.Snippet) is { } index)
        {
            passages.Add(new Passage(index, index, 1, 1));
        }

        List<MediaMoment> moments = [];
        foreach (Passage passage in passages.OrderByDescending(item => item.Coverage).ThenByDescending(item => item.Mentions)
                     .ThenBy(item => item.First))
        {
            (int first, int last) = Widen(timeline, passage.First, passage.Last);
            MediaMoment moment = Build(hit.SourceId, hit.ItemId, hit.Revision, hit.SourceLocator, timeline, first, last);
            if (moments.Any(existing => Overlaps(existing.StartMilliseconds, existing.EndMilliseconds, moment)) ||
                avoid?.Any(shown => Overlaps(shown.Start, shown.End, moment)) == true)
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

    private static bool Overlaps(long start, long end, MediaMoment moment) =>
        start < moment.EndMilliseconds && moment.StartMilliseconds < end;

    /// <summary>Groups the spans that mention query terms into passages: runs of mentions without a long pause between them.</summary>
    private static List<Passage> Passages(MediaTimeline timeline, string[][] terms)
    {
        List<Passage> passages = [];
        if (terms.Length == 0)
        {
            return passages;
        }

        int first = -1, last = -1, mask = 0, mentions = 0;
        void Close()
        {
            if (first >= 0)
            {
                passages.Add(new Passage(first, last, System.Numerics.BitOperations.PopCount((uint)mask) / (double)terms.Length, mentions));
            }
        }

        for (int index = 0; index < timeline.Spans.Count; index++)
        {
            TimedSpan span = timeline.Spans[index];
            int matched = Matches(timeline, span, terms);
            if (matched == 0)
            {
                continue;
            }

            if (first >= 0 && (span.StartMilliseconds - timeline.Spans[last].EndMilliseconds > PassageGapMilliseconds ||
                               span.EndMilliseconds - timeline.Spans[first].StartMilliseconds > PassageMaximumMilliseconds))
            {
                Close();
                first = -1;
                mask = mentions = 0;
            }

            if (first < 0)
            {
                first = index;
            }

            last = index;
            mask |= matched;
            mentions += System.Numerics.BitOperations.PopCount((uint)matched);
        }

        Close();
        return passages;
    }

    private static int Matches(MediaTimeline timeline, TimedSpan span, string[][] terms)
    {
        int end = Math.Min(timeline.Content.Length, span.Offset + span.Length);
        string text = timeline.Content[span.Offset..end];
        int mask = 0;
        for (int term = 0; term < terms.Length; term++)
        {
            if (terms[term].Any(variant => ContainsWordPrefix(text, variant)))
            {
                mask |= 1 << term;
            }
        }

        return mask;
    }

    /// <summary>Widens a passage to sentence boundaries and a watchable length without running into the next topic.</summary>
    private static (int First, int Last) Widen(MediaTimeline timeline, int first, int last)
    {
        IReadOnlyList<TimedSpan> spans = timeline.Spans;
        int from = first;
        while (from > 0 && first - from < LeadSpans && !StartsSentence(timeline, from) &&
               spans[from].StartMilliseconds - spans[from - 1].EndMilliseconds <= SilenceMilliseconds)
        {
            from--;
        }

        long start = spans[from].StartMilliseconds;
        long grace = Math.Max(spans[last].EndMilliseconds, start + MinimumMilliseconds) + SentenceGraceMilliseconds;
        int to = last;
        while (to + 1 < spans.Count)
        {
            TimedSpan next = spans[to + 1];
            bool tooShort = spans[to].EndMilliseconds - start < MinimumMilliseconds;
            bool midSentence = !StartsSentence(timeline, to + 1) && next.EndMilliseconds <= grace;
            if (next.StartMilliseconds - spans[to].EndMilliseconds > SilenceMilliseconds ||
                next.EndMilliseconds - start > MaximumMilliseconds - LeadInMilliseconds - TailMilliseconds ||
                (!tooShort && !midSentence))
            {
                break;
            }

            to++;
        }

        return (from, to);
    }

    /// <summary>True when the span begins a sentence: the transcript before it ends with terminal punctuation or a paragraph break.</summary>
    private static bool StartsSentence(MediaTimeline timeline, int index)
    {
        int position = timeline.Spans[index].Offset - 1;
        while (position >= 0 && char.IsWhiteSpace(timeline.Content[position]))
        {
            if (timeline.Content[position] == '\n')
            {
                return true;
            }

            position--;
        }

        return position < 0 || timeline.Content[position] is '.' or '?' or '!' or '…';
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

    private static bool ContainsWordPrefix(string text, string term)
    {
        // Match a word that starts with the term's stem, so "configure" finds "configuring".
        string stem = term.Length > 5 ? term[..^Math.Min(3, term.Length - 5)] : term;
        int index = 0;
        while ((index = text.IndexOf(stem, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = index + stem.Length;
            if ((index == 0 || !char.IsLetterOrDigit(text[index - 1])) &&
                (stem.Length > 3 || end == text.Length || !char.IsLetterOrDigit(text[end])))
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

    /// <summary>
    /// The term plus any phrase the transcript repeatedly uses that it abbreviates ("ram" → "random access memory"), so a passage
    /// that says the words out loud counts as being about the acronym.
    /// </summary>
    private static string[] Variants(string term, string content)
    {
        if (term.Length is < 2 or > 5 || !term.All(char.IsLetter))
        {
            return [term];
        }

        string[] words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(TrimmedPunctuation))
            .ToArray();
        Dictionary<string, int> phrases = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index + term.Length <= words.Length; index++)
        {
            bool spells = true;
            for (int letter = 0; letter < term.Length && spells; letter++)
            {
                string word = words[index + letter];
                spells = word.Length >= 3 && word.All(char.IsLetter) && !StopWords.Contains(word) &&
                         char.ToLowerInvariant(word[0]) == char.ToLowerInvariant(term[letter]);
            }

            if (spells)
            {
                string phrase = string.Join(' ', words, index, term.Length);
                phrases[phrase] = phrases.GetValueOrDefault(phrase) + 1;
            }
        }

        return [term, .. phrases.Where(pair => pair.Value >= 2).OrderByDescending(pair => pair.Value).Take(2).Select(pair => pair.Key)];
    }

    private static readonly char[] TrimmedPunctuation = ['.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '…', '—', '-'];

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

    private sealed record Passage(int First, int Last, double Coverage, int Mentions);
}
