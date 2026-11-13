namespace Praxis.Domain.Agents;

/// <summary>Evidence a tool returned: the exact source revision and locator an answer may cite.</summary>
public sealed record AgentCitation(Guid SourceId, Guid ItemId, int Revision, string Locator, string SourceName);
