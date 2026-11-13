using System.Text.Json;

namespace Praxis.Application.Agents;

/// <summary>A tool as presented to models: namespaced name, purpose, argument JSON Schema and permission tier.</summary>
public sealed record AgentToolDefinition(string Name, string Description, JsonElement InputSchema, string Tier);
