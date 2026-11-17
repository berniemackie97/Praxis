using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Limits chat history without altering saved messages or splitting earlier user turns.</summary>
internal sealed record AgentContextWindow(IReadOnlyList<AgentMessage> Messages, int OmittedMessages, bool ActiveTrafficExceeded = false)
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

    internal static AgentContextWindow WithActiveToolTraffic(IReadOnlyList<AgentMessage> transcript, int maximumCharacters)
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
        long activeCharacters = Characters(transcript[owner]) + traffic.Sum(Characters);
        if (activeCharacters > maximumCharacters)
        {
            return new AgentContextWindow([transcript[owner]], 0, true);
        }

        int historyCharacters = (int)Math.Max(1, maximumCharacters - traffic.Sum(Characters));
        AgentContextWindow bounded = Select(transcript.Take(owner + 1).ToArray(), historyCharacters);
        return bounded with { Messages = [.. bounded.Messages, .. traffic] };
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
        long payload = message.Content.Length;
        foreach (AgentToolCall call in message.ToolCalls)
        {
            payload += call.CallId.Length + call.Name.Length + call.ArgumentsJson.Length + 32;
        }

        return payload == 0 ? 0 : payload + 64;
    }
}
