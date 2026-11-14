using Praxis.Application.Policy;
using Praxis.Application.Text;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Projects;
using Praxis.Domain.Verification;

namespace Praxis.Application.Projects;

/// <summary>
/// Understands code projects as a whole. Code detects projects and builds a bounded repository map;
/// one strong-tier call describes the project; code verifies every path, entry point and command;
/// the strongest tier corrects a description that fails. A project is re-derived only when its files
/// change, and at most once per interval, so editing code does not spend tokens on every save.
/// </summary>
public sealed class ProjectUnderstander(
    IProjectRepository projects, ITextDerivativeRepository texts, IntakeLadder ladder, IPolicyRepository policy, TimeProvider clock, Func<IReadOnlyList<ProjectSource>, IReadOnlyCollection<string>>? gitRoots = null)
{
    public const string ProcessorKey = "project-understand-v1";
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);
    private const int MaximumSources = 50_000;
    private const int MaximumProjectsPerPass = 3;

    /// <summary>Runs one pass; returns how many projects were (re)understood.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectSource> sources = projects.ReadActiveSources(MaximumSources);
        IReadOnlyList<ProjectCandidate> candidates = ProjectDetector.Detect(sources, gitRoots?.Invoke(sources));
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
        ProjectSource[] CurrentMembers() => [.. projects.ReadCurrentMembers(project.Members)];

        string? ReadText(ProjectSource member) =>
            texts.ReadCurrent(member.ItemId, member.Revision, expectedSourceId: member.SourceId, expectedSha256: member.Digest) is
                { Outcome: "completed", Content: { } content } ? content : null;

        ProjectSource[] localMembers = CurrentMembers();
        RepositoryMap localMap = RepositoryMap.Build(project, localMembers, ReadText);
        if (!localMembers.Any(member => ProjectMarkers.IsMarker(Path.GetFileName(member.Locator))))
        {
            return new ProjectUnderstanding(project.ProjectId, project.Root, project.Name, fingerprint, ProjectUnderstanding.MapOnly,
                "local_repository_map", null, null, [], [], [], localMap.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(300).ToArray(),
                localMap.Languages, localMembers.Length, null, null, 0, 0, clock.GetUtcNow());
        }

        DisclosureGate gate = new(policy, clock);
        Dictionary<string, RepositoryMap> maps = new(StringComparer.Ordinal);
        TieredRequest? RequestForTier(string tier)
        {
            string? providerId = policy.ReadTiers().FirstOrDefault(assignment => assignment.Tier == tier)?.ProviderId;
            if (providerId is null && tier != ModelTiers.Local)
            {
                return null;
            }

            ProjectSource[] current = CurrentMembers();
            ProjectSource[] allowed = tier == ModelTiers.Local || providerId == VerificationProvider.Ollama ? current :
                current.Where(member =>
                {
                    SourceCategory? category = policy.ReadCategory(member.SourceId);
                    return category is { LocalOnly: false, Category: DataCategories.Code or DataCategories.Documents } &&
                        gate.Decide(member.SourceId, providerId, false, DisclosurePurposes.Intake).Decision.Allowed;
                }).ToArray();
            ProjectSource? anchor = allowed.FirstOrDefault(member => ProjectMarkers.IsMarker(Path.GetFileName(member.Locator)));
            if (anchor is null)
            {
                return null;
            }

            RepositoryMap map = RepositoryMap.Build(project, allowed, ReadText);
            maps[tier] = map;
            bool ScopeCurrent() => projects.ReadCurrentMembers(allowed).Count == allowed.Length;

            return new TieredRequest(anchor.SourceId, null, null, ProcessorKey, DisclosurePurposes.Intake,
                ProjectPrompt.Create(map), DisclosureSources: allowed.Select(member => member.SourceId).ToArray(),
                SourceScopeCurrent: ScopeCurrent);
        }

        LadderResult<ProjectDraft> result = await ladder.RunAsync(RequestForTier,
            (content, tier) =>
            {
                if (!maps.TryGetValue(tier, out RepositoryMap? map))
                {
                    return null;
                }

                ProjectDraft? draft = ProjectVerifier.Verify(content, map);
                return draft is null ? null : new LadderCheck<ProjectDraft>(draft, draft.Acceptable);
            }, cancellationToken);
        ProjectDraft? value = result.Value;
        string status = result.Status == LadderResult<ProjectDraft>.Accepted ? ProjectUnderstanding.Understood
            : value is not null ? ProjectUnderstanding.PartiallyVerified
            : ProjectUnderstanding.MapOnly;
        RepositoryMap basis = result.Tier is { } usedTier && maps.TryGetValue(usedTier, out RepositoryMap? selected) ? selected : localMap;
        return new ProjectUnderstanding(project.ProjectId, project.Root, project.Name, fingerprint, status,
            status == ProjectUnderstanding.Understood ? null : result.Reason, value?.Purpose, value?.Summary, value?.Components ?? [],
            value?.EntryPoints ?? [], value?.BuildCommands ?? [], basis.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(300).ToArray(),
            basis.Languages, localMembers.Length, result.Tier, result.ProviderId, result.InputTokens, result.OutputTokens, clock.GetUtcNow());
    }
}
