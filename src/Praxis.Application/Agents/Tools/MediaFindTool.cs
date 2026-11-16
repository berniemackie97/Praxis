using System.Text.Json;
using Praxis.Application.Media;
using Praxis.Application.Search;
using Praxis.Application.Sources;
using Praxis.Domain.Agents;
using Praxis.Domain.Media;
using Praxis.Domain.Search;
using Praxis.Domain.Sources;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// Finds the moments in the owner's videos and recordings where something is said, as playable
/// clips with exact time ranges, or returns a whole recording. Each moment is cited with a
/// <c>time:</c> locator that the chat shows as an inline player. Clips already shown in the conversation are skipped
/// so asking again yields different moments, and a recording can be named by its file name as well as its id.
/// </summary>
public sealed class MediaFindTool(KnowledgeRetriever retriever, MediaMoments moments, ISourceRepository sources,
    IAgentConversationRepository? conversations = null) : IAgentTool
{
    public const string ToolName = "media_find";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        """
        Find moments in the owner's videos, recordings and linked online videos where something is said. Returns clips
        (sourceId, start/end seconds, transcript text) cited with time:START-END locators; the owner sees each cited clip as a player in
        chat and can expand it to the full video. Give query to find moments (optionally within one recording: sourceId, or its file name);
        give only sourceId to get the whole recording. Clips already shown in this chat are skipped, so call again for different ones
        (includeShown=true to allow repeats). Cite the clips you mean with [n]. Do not describe what is on screen; you only see the transcript.
        """,
        ToolArguments.Schema("""
            {"type":"object","properties":{"query":{"type":"string"},"sourceId":{"type":"string"},"collectionId":{"type":"string"},
              "limit":{"type":"integer","minimum":1,"maximum":10},"exactTopic":{"type":"boolean"},"includeShown":{"type":"boolean"}}}
            """), ToolTiers.Read);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        string? query = args.Text("query", 256);
        Guid? sourceId = ResolveSource(args.Text("sourceId", 512));
        bool exactTopic = args.Boolean("exactTopic", false);
        int limit = args.Integer("limit", 5, 1, 10);
        if (query is null && sourceId is null)
        {
            throw new ArgumentException("Provide a query, a sourceId, or both.");
        }

        Dictionary<Guid, List<(long Start, long End)>> shown = args.Boolean("includeShown", false) ? [] : Shown(context.ConversationId);
        List<MediaMoment> found = [];
        int withheld = 0;
        if (sourceId is { } id)
        {
            CurrentFileVersion current = sources.CurrentFile(id) ?? throw new ArgumentException("That source is not an active file source.");
            if (!context.MayDisclose(id))
            {
                return AgentToolOutput.Of(new { error = "withheld_by_disclosure_policy", sourceId = id }, withheld: [new WithheldNotice(1, WithheldNotice.DisclosurePolicy)]);
            }

            if (query is null)
            {
                MediaMoment whole = moments.Whole(id, current.ItemId, current.Revision, current.Locator)
                                    ?? throw new ArgumentException("That source has no transcript or captions yet.");
                found.Add(whole);
            }
            else
            {
                SearchHit self = new(id, current.ItemId, current.Revision, Path.GetFileName(current.Locator), current.Locator, current.Digest, string.Empty);
                found.AddRange(moments.Locate(self, query, limit, exactTopic, shown.GetValueOrDefault(id)));
            }
        }
        else
        {
            Guid? collection = args.Guid("collectionId");
            IReadOnlyList<SearchHit> hits = exactTopic
                ? retriever.SearchTimedText(query!, 100, collection)
                : (await retriever.SearchAsync(query!, 100, collection, cancellationToken)).Hits;
            foreach (SearchHit hit in hits)
            {
                if (found.Count >= limit)
                {
                    break;
                }

                if (!moments.IsMedia(hit))
                {
                    continue;
                }

                if (!context.MayDisclose(hit.SourceId))
                {
                    withheld++;
                    continue;
                }

                found.AddRange(moments.Locate(hit, query!, Math.Min(2, limit - found.Count), exactTopic, shown.GetValueOrDefault(hit.SourceId)));
            }
        }

        int pairedWithheld = found.RemoveAll(moment => moment.MediaSourceId != moment.SourceId && !context.MayDisclose(moment.MediaSourceId));
        withheld += pairedWithheld;
        return AgentToolOutput.Of(new
        {
            moments = found.Select(moment => new
            {
                moment.SourceId,
                mediaSourceId = moment.MediaSourceId,
                source = moment.SourceName,
                startSeconds = moment.StartMilliseconds / 1000.0,
                endSeconds = moment.EndMilliseconds / 1000.0,
                start = TranscriptClock(moment.StartMilliseconds),
                end = TranscriptClock(moment.EndMilliseconds),
                whole = moment.WholeMedia,
                transcript = moment.Text.Length <= 700 ? moment.Text : moment.Text[..700] + "…"
            }),
            note = found.Count > 0 ? null
                : shown.Count > 0 && query is not null ? "No other moment matched beyond the clips already shown in this chat. Try other words, or includeShown=true."
                : "No spoken moment matched. Try other words, or knowledge_search with kind=media."
        },
        found.Select(moment => new AgentCitation(moment.SourceId, moment.ItemId, moment.Revision, moment.Locator, moment.SourceName,
            moment.MediaSourceId == moment.SourceId ? null : moment.MediaSourceId,
            moment.SourceId == moment.MediaSourceId && MediaPlayback.UsesAudioSampleClock(Path.GetExtension(moment.SourceName))
                ? MediaPlayback.AudioSampleTimebase : MediaPlayback.CurrentTimebase)).ToArray(),
        withheld > 0 ? [new WithheldNotice(withheld, WithheldNotice.DisclosurePolicy)] : null);
    }

    /// <summary>
    /// A source id, or the file name of an active source (models often pass the name they were shown). Unknown names are rejected
    /// with the matching choices so the caller can retry.
    /// </summary>
    private Guid? ResolveSource(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Guid.TryParse(value, out Guid id) && id != Guid.Empty)
        {
            return id;
        }

        string name = Path.GetFileName(value.Trim());
        Guid[] named = sources.ActiveFileLocators()
            .Where(item => string.Equals(Path.GetFileName(item.Locator), name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SourceId).Distinct().ToArray();
        return named.Length switch
        {
            1 => named[0],
            0 => throw new ArgumentException($"No recording named '{name}'. Give a sourceId from another tool, or leave sourceId out to search all recordings."),
            _ => throw new ArgumentException($"Several recordings are named '{name}'. Give the sourceId from another tool.")
        };
    }

    /// <summary>The time ranges already cited in this conversation, by transcript source.</summary>
    private Dictionary<Guid, List<(long Start, long End)>> Shown(Guid conversationId)
    {
        Dictionary<Guid, List<(long Start, long End)>> shown = [];
        if (conversations is null)
        {
            return shown;
        }

        foreach (AgentCitation citation in conversations.ReadMessages(conversationId).SelectMany(message => message.Citations))
        {
            if (TimeLocator.TryParse(citation.Locator, out long start, out long end))
            {
                (shown.TryGetValue(citation.SourceId, out List<(long Start, long End)>? ranges) ? ranges : shown[citation.SourceId] = []).Add((start, end));
            }
        }

        return shown;
    }

    private static string TranscriptClock(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
