namespace Praxis.Application.Projects;

/// <summary>A current, active file source considered for project membership.</summary>
public sealed record ProjectSource(Guid SourceId, Guid ItemId, int Revision, string Locator, string Digest);
