namespace Praxis.Domain.Agents;

/// <summary>First-party command metadata shared with the composer; it does not grant permissions.</summary>
public sealed record AgentCommandDefinition(string Name, string Title, string Description, string ArgumentHint,
    string MinimumTier, bool RequiresTools, bool LocalOnly);
