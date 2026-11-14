using System.Security.Cryptography;
using System.Text;
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
            string disclosureFingerprint = DisclosureFingerprint(candidate);
            ProjectUnderstanding? existing = projects.Read(candidate.ProjectId);
            if (existing?.Reason != "source_scope_changed" && existing?.DisclosureFingerprint == disclosureFingerprint &&
                (existing.Fingerprint == fingerprint || clock.GetUtcNow() - existing.UpdatedUtc < MinimumInterval))
            {
                continue;
            }

            ProjectUnderstanding understanding = await UnderstandAsync(candidate, fingerprint, cancellationToken);
            projects.Save(understanding with { DisclosureFingerprint = disclosureFingerprint });
            done++;
        }

        return done;
    }

    private string DisclosureFingerprint(ProjectCandidate project)
    {
        DisclosurePolicy settings = policy.ReadPolicy();
        TierAssignment[] tiers = policy.ReadTiers().Where(tier => tier.Tier is ModelTiers.Strong or ModelTiers.Strongest or ModelTiers.Local)
            .OrderBy(tier => tier.Tier, StringComparer.Ordinal).ToArray();
        StringBuilder state = new();
        foreach (TierAssignment tier in tiers)
        {
            state.Append(tier.Tier).Append('\0').Append(tier.ProviderId).Append('\0').Append(tier.Model).Append('\0').Append(tier.Effort).Append('\n');
        }

        DateTimeOffset now = clock.GetUtcNow();
        foreach (ProjectSource member in project.Members)
        {
            SourceCategory? category = policy.ReadCategory(member.SourceId);
            string kind = category?.Category ?? DataCategories.Unclassified;
            bool localOnly = category?.LocalOnly ?? false;
            state.Append(member.SourceId).Append('\0').Append(kind).Append('\0').Append(localOnly).Append(':');
            foreach (TierAssignment tier in tiers)
            {
                bool local = tier.ProviderId == VerificationProvider.Ollama;
                DisclosureDecision decision = DisclosureEvaluator.Decide(settings, kind, tier.ProviderId, local, DisclosurePurposes.Intake, localOnly, now);
                state.Append(decision.Allowed ? '1' : '0');
            }

            state.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
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
        bool IsEligible(ProjectSource member, string providerId) =>
            providerId == VerificationProvider.Ollama ||
            (policy.ReadCategory(member.SourceId) is { LocalOnly: false, Category: DataCategories.Code or DataCategories.Documents } &&
             gate.Decide(member.SourceId, providerId, false, DisclosurePurposes.Intake).Decision.Allowed);

        Dictionary<string, RepositoryMap> maps = new(StringComparer.Ordinal);
        Dictionary<string, ProjectSummarySource[]> scopes = new(StringComparer.Ordinal);
        ProjectSummarySource CaptureScope(ProjectSource member)
        {
            SourceCategory? category = policy.ReadCategory(member.SourceId);
            return new ProjectSummarySource(member.SourceId, member.ItemId, member.Revision, member.Locator, member.Digest,
                category?.Category ?? DataCategories.Unclassified, category?.LocalOnly ?? false);
        }

        bool ScopeCurrent(IReadOnlyList<ProjectSummarySource> sources) =>
            projects.ReadCurrentMembers(sources.Select(source =>
                new ProjectSource(source.SourceId, source.ItemId, source.Revision, source.Locator, source.Digest)).ToArray()).Count == sources.Count &&
            sources.All(source =>
            {
                SourceCategory? category = policy.ReadCategory(source.SourceId);
                return (category?.Category ?? DataCategories.Unclassified) == source.Category &&
                       (category?.LocalOnly ?? false) == source.LocalOnly;
            });
        TieredRequest? RequestForTier(string tier)
        {
            string? configuredProvider = policy.ReadTiers().FirstOrDefault(assignment => assignment.Tier == tier)?.ProviderId;
            if (configuredProvider is not { } providerId || (tier == ModelTiers.Local && providerId != VerificationProvider.Ollama))
            {
                return null;
            }

            ProjectSource[] current = CurrentMembers();
            ProjectSource[] allowed = current.Where(member => IsEligible(member, providerId)).ToArray();
            ProjectSource? anchor = allowed.FirstOrDefault(member => ProjectMarkers.IsMarker(Path.GetFileName(member.Locator)));
            if (anchor is null)
            {
                return null;
            }

            RepositoryMap map = RepositoryMap.Build(project, allowed, ReadText);
            maps[tier] = map;
            ProjectSummarySource[] sourceScope = allowed.Select(CaptureScope).ToArray();
            scopes[tier] = sourceScope;
            bool Current() => ScopeCurrent(sourceScope) && allowed.All(member => IsEligible(member, providerId));

            return new TieredRequest(anchor.SourceId, null, null, ProcessorKey, DisclosurePurposes.Intake,
                ProjectPrompt.Create(map), DisclosureSources: allowed.Select(member => member.SourceId).ToArray(),
                SourceScopeCurrent: Current);
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
        ProjectSummarySource[]? evidence = result.Tier is { } evidenceTier && scopes.TryGetValue(evidenceTier, out ProjectSummarySource[]? attached)
            ? attached
            : null;
        if (result.Tier is { } completedTier && result.ProviderId is { } providerId)
        {
            bool stillAuthorized = policy.ReadTiers().FirstOrDefault(assignment => assignment.Tier == completedTier)?.ProviderId == providerId &&
                evidence is { Length: > 0 } && ScopeCurrent(evidence) &&
                basis.Disclosed.All(member => IsEligible(member, providerId));
            if (!stillAuthorized)
            {
                ProjectSource[] current = CurrentMembers();
                RepositoryMap local = RepositoryMap.Build(project, current, ReadText);
                return new ProjectUnderstanding(project.ProjectId, project.Root, project.Name, fingerprint, ProjectUnderstanding.MapOnly,
                    "source_scope_changed", null, null, [], [], [], local.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(300).ToArray(),
                    local.Languages, current.Length, null, null, result.InputTokens, result.OutputTokens, clock.GetUtcNow());
            }
        }

        return new ProjectUnderstanding(project.ProjectId, project.Root, project.Name, fingerprint, status,
            status == ProjectUnderstanding.Understood ? null : result.Reason, value?.Purpose, value?.Summary, value?.Components ?? [],
            value?.EntryPoints ?? [], value?.BuildCommands ?? [], basis.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(300).ToArray(),
            basis.Languages, localMembers.Length, result.Tier, result.ProviderId, result.InputTokens, result.OutputTokens, clock.GetUtcNow(), evidence);
    }
}
