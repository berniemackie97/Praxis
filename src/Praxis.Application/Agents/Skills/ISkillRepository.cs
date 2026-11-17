namespace Praxis.Application.Agents.Skills;

public interface ISkillRepository
{
    IReadOnlyList<SkillDescriptor> Installed();
    IReadOnlySet<string> DisabledNames();
    SkillRevision? Read(Guid id);
    SkillRevision? ReadInstalled(string name, bool enabledOnly);
    IReadOnlyList<SkillRevisionSummary> History(string? name, bool pendingOnly, int limit);
    SkillRevisionSummary Propose(SkillDocument document, Guid? conversationId, string rationale, Guid? expectedBase, Guid? experienceId = null);
    bool Resolve(Guid id, bool accept);
    bool SetEnabled(string name, bool enabled);
}
