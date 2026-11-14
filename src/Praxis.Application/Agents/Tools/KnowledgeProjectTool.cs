using System.Text.Json;
using Praxis.Application.Projects;
using Praxis.Domain.Agents;
using Praxis.Domain.Projects;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// The owner's code projects understood as a whole: purpose, architecture, components, entry points,
/// build commands and declared dependencies, each path and command verified against the project.
/// Files are cited so the agent can open them with knowledge_open.
/// </summary>
public sealed class KnowledgeProjectTool(IProjectRepository projects) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("knowledge_project",
        """
        The owner's code projects (folders with a solution, package manifest or build file). Without projectId: list projects with purpose,
        languages and size. With projectId (or name): purpose, summary, components with paths, entry points, verified build commands and
        declared dependencies. Then open files with knowledge_open or search them with knowledge_search kind=code.
        """,
        ToolArguments.Schema("""{"type":"object","properties":{"projectId":{"type":"string"},"name":{"type":"string"}}}"""),
        ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        string? id = args.Text("projectId", 32);
        string? name = args.Text("name", 256);
        IReadOnlyList<ProjectUnderstanding> all = projects.List();
        if (id is null && name is null)
        {
            return Task.FromResult(AgentToolOutput.Of(new
            {
                projects = all.Select(project => new
                {
                    project.ProjectId,
                    project.Name,
                    project.Purpose,
                    project.Status,
                    files = project.Members,
                    languages = project.Languages.OrderByDescending(pair => pair.Value).Take(4).Select(pair => pair.Key)
                }),
                note = all.Count == 0 ? "No code projects found yet. A project is a watched folder with a solution, package manifest or build file." : null
            }));
        }

        ProjectUnderstanding project = all.FirstOrDefault(candidate => candidate.ProjectId == id ||
                                                                       (name is not null && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                                       ?? throw new ArgumentException("No project with that projectId or name; call knowledge_project without arguments to list them.");
        return Task.FromResult(AgentToolOutput.Of(new
        {
            project.ProjectId,
            project.Name,
            project.Root,
            project.Status,
            project.Purpose,
            project.Summary,
            components = project.Components,
            entryPoints = project.EntryPoints,
            buildCommands = project.BuildCommands,
            dependencies = project.Dependencies.Take(120),
            project.Languages,
            files = project.Members,
            project.Tier,
            project.UpdatedUtc
        }));
    }
}
