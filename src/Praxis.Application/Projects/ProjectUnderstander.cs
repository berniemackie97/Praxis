using Praxis.Application.Policy;
using Praxis.Application.Text;
using Praxis.Domain.Policy;
using Praxis.Domain.Projects;

namespace Praxis.Application.Projects;

/// <summary>
/// Understands code projects as a whole. Code detects projects and builds a bounded repository map;
/// one strong-tier call describes the project; code verifies every path, entry point and command;
/// the strongest tier corrects a description that fails. A project is re-derived only when its files
/// change, and at most once per interval, so editing code does not spend tokens on every save.
/// </summary>
public sealed class ProjectUnderstander(
    IProjectRepository projects, ITextDerivativeRepository texts, IntakeLadder ladder, IPolicyRepository policy, TimeProvider clock)
{
    public const string ProcessorKey = "project-understand-v1";
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    private const int MaximumSources = 50_000;
    private const int MaximumProjectsPerPass = 3;

    /// <summary>Runs one pass; returns how many projects were (re)understood.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectCandidate> candidates = ProjectDetector.Detect(projects.ReadActiveSources(MaximumSources));
        projects.RemoveExcept(candidates.Select(candidate => candidate.ProjectId).ToArray());
        int done = 0;
        foreach (ProjectCandidate candidate in candidates)
        {
            if (done == MaximumProjectsPerPass)
            {
                break;
            }

            string fingerprint = ProjectDetector.Fingerprint(candidate);
            ProjectUnderstanding? existing = projects.Read(candidate.ProjectId);
            if (existing?.Fingerprint == fingerprint || (existing is not null && clock.GetUtcNow() - existing.UpdatedUtc < MinimumInterval))
            {
                continue;
            }

            projects.Save(await UnderstandAsync(candidate, fingerprint, cancellationToken));
            done++;
        }

        return done;
    }

    public async Task<ProjectUnderstanding> UnderstandAsync(ProjectCandidate project, string fingerprint, CancellationToken cancellationToken)
    {
        // Only files whose category may leave for code work are shown to a remote model.
        ProjectSource[] disclosed = project.Members.Where(member => policy.ReadCategory(member.SourceId) is not { } category ||
            (!category.LocalOnly && category.Category is DataCategories.Code or DataCategories.Documents)).ToArray();
        RepositoryMap map = RepositoryMap.Build(project, disclosed, member =>
            texts.ReadCurrent(member.ItemId, member.Revision) is { Outcome: "completed", Content: { } content } derivative && derivative.SourceId == member.SourceId
                ? content
                : null);
        ProjectSource anchor = disclosed.FirstOrDefault(member => ProjectMarkers.IsMarker(Path.GetFileName(member.Locator))) ?? project.Members[0];
        LadderResult<ProjectDraft> result = await ladder.RunAsync(
            new TieredRequest(anchor.SourceId, null, null, ProcessorKey, DisclosurePurposes.Intake, ProjectPrompt.Create(map), DataCategories.Code),
            (content, _) =>
            {
                ProjectDraft? draft = ProjectVerifier.Verify(content, map);
                return draft is null ? null : new LadderCheck<ProjectDraft>(draft, draft.Acceptable);
            },
            cancellationToken);
        ProjectDraft? value = result.Value;
        string status = result.Status == LadderResult<ProjectDraft>.Accepted ? ProjectUnderstanding.Understood
            : value is not null ? ProjectUnderstanding.PartiallyVerified
            : ProjectUnderstanding.MapOnly;
        return new ProjectUnderstanding(project.ProjectId, project.Root, project.Name, fingerprint, status,
            status == ProjectUnderstanding.Understood ? null : result.Reason, value?.Purpose, value?.Summary, value?.Components ?? [],
            value?.EntryPoints ?? [], value?.BuildCommands ?? [], map.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(300).ToArray(),
            map.Languages, project.Members.Count, result.Tier, result.ProviderId, result.InputTokens, result.OutputTokens, clock.GetUtcNow());
    }
}
