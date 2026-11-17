using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Praxis.Domain.Knowledge;

/// <summary>Fences a reviewed correction to the entity and evidence state actually inspected.</summary>
public sealed record KnowledgeSnapshot(Guid EntityId, string Name, string? Type, string Fingerprint)
{
    public static KnowledgeSnapshot Capture(EntityDetail entity)
    {
        string state = JsonSerializer.Serialize(new
        {
            entity.Summary.Name,
            entity.Summary.EntityType,
            entity.Summary.TypeOrigin,
            entity.Summary.Status,
            aliases = entity.Aliases.Order(StringComparer.Ordinal),
            mentions = entity.Mentions.OrderBy(mention => mention.ProposalId),
            relations = entity.Relations.OrderBy(relation => relation.RelationId)
        });
        return new KnowledgeSnapshot(entity.Summary.EntityId, entity.Summary.Name, entity.Summary.EntityType,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state))));
    }
}
