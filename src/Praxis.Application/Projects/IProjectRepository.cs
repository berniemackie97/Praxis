using Praxis.Domain.Projects;

namespace Praxis.Application.Projects;

/// <summary>Project understandings and the active file sources projects are detected from.</summary>
public interface IProjectRepository
{
    /// <summary>The current revision of every active file source, bounded.</summary>
    IReadOnlyList<ProjectSource> ReadActiveSources(int limit);

    ProjectUnderstanding? Read(string projectId);

    IReadOnlyList<ProjectUnderstanding> List();

    void Save(ProjectUnderstanding project);

    /// <summary>Removes understandings of projects that no longer exist.</summary>
    int RemoveExcept(IReadOnlyCollection<string> projectIds);
}
