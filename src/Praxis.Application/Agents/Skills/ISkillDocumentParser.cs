namespace Praxis.Application.Agents.Skills;

public interface ISkillDocumentParser
{
    SkillDocument Parse(string markdown);
}
