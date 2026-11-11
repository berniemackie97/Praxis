using Praxis.Notes.Model;

namespace Praxis.Notes.Vaults;

public sealed record VaultUpdate(KnowledgeCollection Collection, IReadOnlyList<VaultChange> Changes, int AddedLinks = 0, IReadOnlyList<AttachmentReference>? RemovedLinkPairs = null)
{
    public int RemovedLinks => RemovedLinkPairs?.Count ?? 0;
}
