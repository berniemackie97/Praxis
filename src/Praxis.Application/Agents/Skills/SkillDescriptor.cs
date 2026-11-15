namespace Praxis.Application.Agents.Skills;

public sealed record SkillDescriptor(string Name, string Description, string Digest, Guid? RevisionId, bool Bundled, bool Enabled);
