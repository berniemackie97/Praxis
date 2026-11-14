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
/// <c>time:</c> locator that the chat shows as an inline player.
/// </summary>
public sealed class MediaFindTool(KnowledgeRetriever retriever, MediaMoments moments, ISourceRepository sources) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("media_find",
        """
        Find moments in the owner's videos, recordings and linked online videos where something is said or shown in speech. Returns clips
        (sourceId, start/end seconds, transcript text) cited with time:START-END locators; the owner sees each cited clip as a player in
        chat and can expand it to the full video. Give query to find moments (optionally within one sourceId); give only sourceId to get the
        whole recording. Cite the clips you mean with [n]. Do not describe what is on screen; you only see the transcript.
        """,
        ToolArguments.Schema("""
            {"type":"object","properties":{"query":{"type":"string"},"sourceId":{"type":"string"},"collectionId":{"type":"string"},
              "limit":{"type":"integer","minimum":1,"maximum":10}}}
            """), ToolTiers.Read);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        string? query = args.Text("query", 256);
        Guid? sourceId = args.Guid("sourceId");
        int limit = args.Integer("limit", 5, 1, 10);
        if (query is null && sourceId is null)
        {
            throw new ArgumentException("Provide a query, a sourceId, or both.");
        }

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
                found.AddRange(moments.Locate(self, query, limit));
            }
        }
        else
        {
            (IReadOnlyList<SearchHit> hits, string _) = await retriever.SearchAsync(query!, 100, args.Guid("collectionId"), cancellationToken);
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

                found.AddRange(moments.Locate(hit, query!, Math.Min(2, limit - found.Count)));
            }
        }

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
            note = found.Count == 0 ? "No spoken moment matched. Try other words, or knowledge_search with kind=media." : null
        },
        found.Select(moment => new AgentCitation(moment.SourceId, moment.ItemId, moment.Revision, moment.Locator, moment.SourceName)).ToArray(),
        withheld > 0 ? [new WithheldNotice(withheld, WithheldNotice.DisclosurePolicy)] : null);
    }

    private static string TranscriptClock(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
