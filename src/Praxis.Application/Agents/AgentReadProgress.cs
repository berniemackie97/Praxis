using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>Tracks successful knowledge reads for one user request without retaining their contents.</summary>
public sealed class AgentReadProgress
{
    private readonly Dictionary<string, ReadReceipt> completed = new(StringComparer.Ordinal);

    public int ConsecutiveRepeats { get; private set; }

    public bool TryRepeat(AgentToolCall call, Func<Guid, bool> mayDisclose, out string previousCallId)
    {
        previousCallId = "";
        string? key = Key(call);
        if (key is null || !completed.TryGetValue(key, out ReadReceipt? prior) ||
            prior.Citations.Any(citation => !mayDisclose(citation.SourceId) ||
                citation.PlaybackSourceId is { } media && !mayDisclose(media)))
        {
            return false;
        }

        previousCallId = prior.CallId;
        ConsecutiveRepeats++;
        return true;
    }

    public void Record(AgentToolCall call, AgentToolOutput output, bool successful)
    {
        ConsecutiveRepeats = 0;
        if (!successful || output.Withheld.Count > 0 || Key(call) is not { } key)
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(output.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.TryGetProperty("error", out _))
            {
                return;
            }
        }
        catch (JsonException)
        {
            return;
        }

        completed.TryAdd(key, new ReadReceipt(call.CallId, output.Citations.ToArray()));
    }

    private static string? Key(AgentToolCall call)
    {
        if (call.Name is not ("knowledge_search" or "knowledge_open"))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(call.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var arguments = document.RootElement.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new { property.Name, property.Value });
            return call.Name + ":" + JsonSerializer.Serialize(arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ReadReceipt(string CallId, IReadOnlyList<AgentCitation> Citations);
}
