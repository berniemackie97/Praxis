using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Limits chat history without altering saved messages or splitting earlier user turns.</summary>
internal sealed record AgentContextWindow(IReadOnlyList<AgentMessage> Messages, int OmittedMessages, bool ActiveTrafficExceeded = false,
    bool ApproachingEvidenceLimit = false)
{
    // A revoked citation taints the whole generated turn, including its tool results. Keep the owner's
    // request as history, but do not replay an assistant answer that embeds currently withheld data.
    internal static IReadOnlyList<AgentMessage> ExcludeWithheldTurns(IReadOnlyList<AgentMessage> messages, Func<Guid, bool> allowed)
    {
        List<AgentMessage> visible = [];
        List<AgentMessage> turn = [];
        Dictionary<Guid, bool> decisions = [];

        void Flush()
        {
            bool withheld = turn.SelectMany(message => message.Citations).Any(citation =>
            {
                if (!decisions.TryGetValue(citation.SourceId, out bool permit))
                {
                    decisions[citation.SourceId] = permit = allowed(citation.SourceId);
                }

                return !permit || citation.PlaybackSourceId is { } media && !allowed(media);
            });
            visible.AddRange(withheld ? turn.Where(message => message.Role == AgentRoles.User &&
                !message.Content.StartsWith("Answer check failed.", StringComparison.Ordinal)) : turn);
            turn.Clear();
        }

        foreach (AgentMessage message in messages)
        {
            if (message.Role == AgentRoles.User && !message.Content.StartsWith("Answer check failed.", StringComparison.Ordinal))
            {
                Flush();
            }

            turn.Add(message);
        }

        Flush();
        return visible;
    }

    internal static AgentContextWindow WithActiveToolTraffic(IReadOnlyList<AgentMessage> transcript, int maximumCharacters,
        int nextEvidenceCharacters = 0)
    {
        int owner = transcript.Count - 1;
        while (owner > 0 && transcript[owner].Role != AgentRoles.User)
        {
            owner--;
        }

        if (transcript[owner].Role != AgentRoles.User)
        {
            throw new ArgumentException("An active chat request must contain an owner message.", nameof(transcript));
        }

        IReadOnlyList<AgentMessage> traffic = transcript.Skip(owner + 1).ToArray();
        long trafficCharacters = traffic.Sum(Characters);
        long activeCharacters = Characters(transcript[owner]) + trafficCharacters;
        if (activeCharacters > maximumCharacters)
        {
            return new AgentContextWindow([transcript[owner]], 0, true);
        }

        int historyCharacters = (int)Math.Max(1, maximumCharacters - trafficCharacters);
        AgentContextWindow bounded = Select(transcript.Take(owner + 1).ToArray(), historyCharacters);
        return bounded with
        {
            Messages = [.. bounded.Messages, .. traffic],
            ApproachingEvidenceLimit = nextEvidenceCharacters > 0 && activeCharacters + nextEvidenceCharacters > maximumCharacters
        };
    }

    internal static AgentContextWindow ForEvidenceSynthesis(IReadOnlyList<AgentMessage> transcript, int maximumCharacters)
    {
        int owner = transcript.Count - 1;
        while (owner > 0 && transcript[owner].Role != AgentRoles.User)
        {
            owner--;
        }

        if (transcript[owner].Role != AgentRoles.User)
        {
            throw new ArgumentException("An active chat request must contain an owner message.", nameof(transcript));
        }

        AgentMessage request = transcript[owner];
        long remaining = maximumCharacters - Characters(request);
        Dictionary<string, AgentToolCall> calls = transcript.Skip(owner + 1)
            .Where(message => message.Role == AgentRoles.Assistant)
            .SelectMany(message => message.ToolCalls)
            .DistinctBy(call => call.CallId)
            .ToDictionary(call => call.CallId, StringComparer.Ordinal);
        var receipts = transcript.Skip(owner + 1).Select((message, index) => (message, index))
            .Where(entry => entry.message.Role == AgentRoles.Tool && entry.message.ToolCallId is not null &&
                calls.ContainsKey(entry.message.ToolCallId))
            .Select(entry => (entry.index, result: entry.message, call: calls[entry.message.ToolCallId!]))
            .ToArray();
        Dictionary<Guid, int> searchRanks = [];
        foreach (var receipt in receipts.Where(entry => entry.call.Name == "knowledge_search"))
        {
            using JsonDocument document = JsonDocument.Parse(receipt.result.Content);
            if (!document.RootElement.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("results", out JsonElement matches) ||
                matches.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement match in matches.EnumerateArray())
            {
                if (match.TryGetProperty("sourceId", out JsonElement source) && source.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(source.GetString(), out Guid id))
                {
                    searchRanks.TryAdd(id, searchRanks.Count);
                }
            }
        }

        int SourceRank(AgentMessage message)
        {
            using JsonDocument document = JsonDocument.Parse(message.Content);
            if (document.RootElement.TryGetProperty("result", out JsonElement result) &&
                result.ValueKind == JsonValueKind.Object && result.TryGetProperty("sourceId", out JsonElement source) &&
                source.ValueKind == JsonValueKind.String && Guid.TryParse(source.GetString(), out Guid id) &&
                searchRanks.TryGetValue(id, out int rank))
            {
                return rank;
            }

            return int.MaxValue;
        }

        bool hasSourceReads = receipts.Any(entry => entry.call.Name == "knowledge_open");
        var candidates = receipts.Where(entry => !hasSourceReads || entry.call.Name != "knowledge_search")
            .OrderBy(entry => entry.call.Name == "knowledge_open" ? 0 : 1)
            .ThenBy(entry => entry.call.Name == "knowledge_open" ? SourceRank(entry.result) : int.MaxValue)
            .ThenByDescending(entry => entry.index);
        List<(int Index, AgentMessage Assistant, AgentMessage Result)> retained = [];
        foreach (var candidate in candidates)
        {
            AgentMessage assistant = AgentMessage.FromAssistant("", [candidate.call]);
            long length = Characters(assistant) + Characters(candidate.result);
            if (length > remaining)
            {
                continue;
            }

            retained.Add((candidate.index, assistant, candidate.result));
            remaining -= length;
        }

        if (retained.Count == 0)
        {
            return new AgentContextWindow([request], 0, true);
        }

        AgentMessage[] evidence = retained.OrderBy(item => item.Index)
            .SelectMany(item => new[] { item.Assistant, item.Result }).ToArray();
        return new AgentContextWindow([request, .. evidence], 0);
    }

    internal static AgentContextWindow Select(IReadOnlyList<AgentMessage> transcript, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (transcript.Count == 0 || transcript[^1].Role != AgentRoles.User)
        {
            throw new ArgumentException("A chat request must end with the current owner message.", nameof(transcript));
        }

        int start = transcript.Count - 1;
        long remaining = maximumCharacters - Characters(transcript[start]);

        while (start > 0)
        {
            int previous = start - 1;
            while (previous > 0 && !StartsTurn(transcript[previous]))
            {
                previous--;
            }

            if (!StartsTurn(transcript[previous]))
            {
                break;
            }

            long length = 0;
            for (int index = previous; index < start; index++)
            {
                length += Characters(transcript[index]);
            }

            if (length > remaining)
            {
                break;
            }

            remaining -= length;
            start = previous;
        }

        List<AgentMessage> selected = [];
        if (start > 0 && transcript[0].Role == AgentRoles.Summary && Characters(transcript[0]) <= remaining)
        {
            selected.Add(transcript[0]);
        }

        selected.AddRange(transcript.Skip(start));
        int forwarded = transcript.Count(IsForwarded);
        int retained = selected.Count(IsForwarded);
        return new AgentContextWindow(selected, forwarded - retained);
    }

    private static bool StartsTurn(AgentMessage message) =>
        message.Role == AgentRoles.User && !message.Content.StartsWith("Answer check failed.", StringComparison.Ordinal);

    private static bool IsForwarded(AgentMessage message) =>
        message.Role != AgentRoles.Tool && message.Content.Length > 0;

    private static long Characters(AgentMessage message)
    {
        long payload = message.Content.Length + (message.ToolCallId?.Length ?? 0);
        foreach (AgentToolCall call in message.ToolCalls)
        {
            payload += call.CallId.Length + call.Name.Length + call.ArgumentsJson.Length + 32;
        }

        return payload == 0 ? 0 : payload + 64;
    }
}
