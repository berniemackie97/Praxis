using System.Text.Json;
using Praxis.Application.Links;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// Saves a web link the owner gave in this conversation; Praxis then fetches and understands it.
/// The URL must appear verbatim in one of the owner's own messages, so a document, page or tool
/// result can never make the agent fetch an address (with private data in its query) on its own.
/// </summary>
public sealed class KnowledgeSaveLinkTool(ISavedLinkWriter writer, IAgentConversationRepository conversations, ILinkRepository links) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("knowledge_save_link",
        """
        Save a web link the owner pasted in this conversation (article, documentation, YouTube video…) into their second brain. Praxis
        fetches it in the background and it becomes searchable and citable; YouTube links keep their captions so media_find can find
        moments. Only URLs the owner wrote themselves can be saved. Optional title.
        """,
        ToolArguments.Schema("""{"type":"object","properties":{"url":{"type":"string"},"title":{"type":"string"}},"required":["url"]}"""),
        ToolTiers.Propose);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        string text = args.RequireText("url", SavedLink.MaximumUrlLength);
        Uri url = SavedLink.Parse(text) ?? throw new ArgumentException("Give an http or https URL without a user name or password.");
        bool fromOwner = conversations.ReadMessages(context.ConversationId)
            .Any(message => message.Role == AgentRoles.User && message.Content.Contains(text, StringComparison.Ordinal));
        if (!fromOwner)
        {
            return AgentToolOutput.Error("url_not_from_owner", "Only links the owner pasted in this conversation can be saved. Ask the owner to paste it.");
        }

        if (links.ReadSettings() is { Mode: LinkSettings.Off })
        {
            return AgentToolOutput.Error("links_off", "Link fetching is turned off in Settings → Privacy.");
        }

        string path = await writer.SaveAsync(url, args.Text("title", 120), cancellationToken);
        return AgentToolOutput.Of(new
        {
            saved = true,
            url = url.AbsoluteUri,
            file = Path.GetFileName(path),
            note = "Queued for background fetching and intake. This receipt does not mean the content was learned. Search and open the captured source to verify coverage; a video without captions may contain metadata only."
        });
    }
}
