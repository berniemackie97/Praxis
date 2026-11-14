using Praxis.Domain.Projects;

namespace Praxis.Application.Projects;

/// <summary>Project understandings and the active file sources projects are detected from.</summary>
public interface IProjectRepository
{
    /// <summary>The current revision of every active file source, bounded.</summary>
    IReadOnlyList<ProjectSource> ReadActiveSources(int limit);

    /// <summary>Returns only active members still matching the supplied source, revision, path and digest.</summary>
    IReadOnlyList<ProjectSource> ReadCurrentMembers(IReadOnlyList<ProjectSource> expected);

    ProjectUnderstanding? Read(string projectId);

    IReadOnlyList<ProjectUnderstanding> List();

    void Save(ProjectUnderstanding project);

    /// <summary>Removes understandings of projects that no longer exist.</summary>
    int RemoveExcept(IReadOnlyCollection<string> projectIds);
}
