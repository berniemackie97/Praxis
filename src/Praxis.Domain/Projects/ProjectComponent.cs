namespace Praxis.Domain.Projects;

/// <summary>A part of a code project: its name, the folder or file it lives in, and what it does.</summary>
public sealed record ProjectComponent(string Name, string Path, string Role);
