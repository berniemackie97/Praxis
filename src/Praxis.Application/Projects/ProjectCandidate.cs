namespace Praxis.Application.Projects;

/// <summary>A detected code project: its root folder, a stable id, and the sources beneath it.</summary>
public sealed record ProjectCandidate(string ProjectId, string Root, string Name, IReadOnlyList<ProjectSource> Members)
{
    /// <summary>The path of a member relative to the root, with forward slashes.</summary>
    public string Relative(ProjectSource member) => Path.GetRelativePath(Root, member.Locator).Replace('\\', '/');
}
