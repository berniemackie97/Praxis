namespace Praxis.Application.Agents.Capabilities;

public interface IInstalledProgramProbe
{
    bool IsAvailable(string program);
}
