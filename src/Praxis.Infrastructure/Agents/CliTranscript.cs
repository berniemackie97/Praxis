using System.Text;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Agents;

/// <summary>Renders a neutral transcript as the single prompt a CLI agent reads on stdin.</summary>
internal static class CliTranscript
{
    internal static string Render(IReadOnlyList<AgentMessage> transcript)
    {
        StringBuilder text = new("Conversation so far (the last user message is the current request):\n\n");
        foreach (AgentMessage message in transcript.Where(message => message.Role != AgentRoles.Tool))
        {
            string label = message.Role switch
            {
                AgentRoles.User => "Owner",
                AgentRoles.Summary => "Summary of earlier conversation",
                _ => "Praxis"
            };
            if (message.Content.Length > 0)
            {
                text.Append(label).Append(": ").Append(message.Content).Append("\n\n");
            }
        }

        text.Append("Use the praxis tools to answer the owner's latest request.");
        return text.ToString();
    }
}
