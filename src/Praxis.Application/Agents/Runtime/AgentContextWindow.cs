using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Limits CLI history without altering saved messages or splitting earlier user turns.</summary>
internal sealed record AgentContextWindow(IReadOnlyList<AgentMessage> Messages, int OmittedMessages)
{
    internal static AgentContextWindow ForCli(IReadOnlyList<AgentMessage> transcript, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (transcript.Count == 0 || transcript[^1].Role != AgentRoles.User)
        {
            throw new ArgumentException("A CLI request must end with the current owner message.", nameof(transcript));
        }

        int start = transcript.Count - 1;
        int remaining = maximumCharacters - Characters(transcript[start]);
        if (remaining < 0)
        {
            throw new ArgumentException("The current request exceeds the chat context limit.", nameof(transcript));
        }

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

            int length = 0;
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

    private static int Characters(AgentMessage message) =>
        IsForwarded(message) ? message.Content.Length + 64 : 0;
}
