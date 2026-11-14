namespace Praxis.Domain.Projects;

/// <summary>The exact source revision and policy category used to derive a project description.</summary>
public sealed record ProjectSummarySource(
    Guid SourceId, Guid ItemId, int Revision, string Locator, string Digest, string Category, bool LocalOnly);
