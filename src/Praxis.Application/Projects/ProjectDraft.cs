using Praxis.Domain.Projects;

namespace Praxis.Application.Projects;

/// <summary>A project understanding after code verification, with how many stated items were dropped.</summary>
public sealed record ProjectDraft(
    string Purpose, string Summary, IReadOnlyList<ProjectComponent> Components, IReadOnlyList<string> EntryPoints,
    IReadOnlyList<string> BuildCommands, int Verified, int Rejected, IReadOnlyList<string> Problems)
{
    /// <summary>Acceptable when at most a quarter of stated items failed verification.</summary>
    public bool Acceptable => Purpose.Length > 0 && Rejected * 4 <= Math.Max(1, Verified + Rejected);
}
