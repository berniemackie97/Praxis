using Praxis.Domain.Knowledge;

namespace Praxis.Domain.Agents;

/// <summary>Application-observed command effects. Model prose cannot populate this receipt.</summary>
public sealed record AgentCommandReceipt(string Name, string Outcome, int Candidates, IReadOnlyList<KnowledgeSnapshot> Reviewed,
    IReadOnlyList<Guid> ReviewItems, int AppliedChanges, string Summary, Guid? OwnerCorrectionId = null);
