using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Agents.Tools;

/// <summary>Entity metadata can summarize several sources; remote sessions need the whole evidence scope.</summary>
internal static class KnowledgeDisclosure
{
    internal static bool Visible(IKnowledgeRepository knowledge, Guid id, AgentToolContext context) =>
        context.BackendIsLocal || knowledge.GetEntity(id) is { } entity && Visible(entity, context);

    internal static bool Visible(EntityDetail entity, AgentToolContext context) =>
        context.BackendIsLocal || entity.Mentions.Count > 0 && entity.Mentions.All(mention => context.MayDisclose(mention.SourceId));
}
