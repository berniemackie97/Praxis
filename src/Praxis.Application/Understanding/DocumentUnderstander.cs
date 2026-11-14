using Praxis.Application.Policy;
using Praxis.Application.Understanding.Profiles;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Understands a document as one hand-off between tiers, span by span. The local tier drafts what
/// it can read quickly (private and free); code verifies every quote; the strong tier reviews that
/// draft and writes only corrections (or reads the span itself when there is no draft); the
/// strongest tier sees the strong result and the verification problems and corrects again, only
/// when needed. Spans are merged in code; a long document's span summaries are combined in one
/// small call. Each tier's contribution is recorded as the document's cascade.
/// </summary>
public sealed class DocumentUnderstander(IntakeLadder ladder, RecordProfiles profiles, LocalDraftPolicy? draftPolicy = null, LocalPace? pace = null)
{
    /// <summary>Spans up to this size get a local draft; longer ones are read by the strong tier directly.</summary>
    public const int LocalDraftCharacters = 6_000;
    private const int LocalContextTokens = 8_192;
    private const int LocalOutputTokens = 2_048;

    public async Task<UnderstandingOutcome> UnderstandAsync(
        ProcessingLease lease, LeasedUnderstandingSource source, string text, DocumentProfile profile, CancellationToken cancellationToken)
    {
        IReadOnlyList<IRecordProfile> requested = profiles.Requested(profile.RecordKinds);
        string[] kinds = requested.Select(record => record.Kind).ToArray();
        (IReadOnlyList<DocumentChunk> chunks, bool complete) = DocumentChunker.Split(text);
        List<(UnderstandingDraft Draft, LadderResult<UnderstandingDraft> Result)> drafts = [];
        List<CascadeStep> steps = [];
        int input = 0;
        int output = 0;
        bool partial = false;
        LadderResult<UnderstandingDraft>? stopped = null;
        foreach (DocumentChunk chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inference.ChatRequest reading = UnderstandingPrompt.Request(source.FileName, profile, requested, text, chunk, chunks.Count);

            // 1. Local draft, when the span is small enough to read quickly on this Mac.
            UnderstandingDraft? baseline = null;
            LadderResult<UnderstandingDraft>? localResult = null;
            if (draftPolicy?.ShouldDraft(chunk.Length) ?? chunk.Length <= LocalDraftCharacters)
            {
                (LadderCheck<UnderstandingDraft>? local, TieredOutcome localOutcome) = await ladder.DraftAsync(
                    Request(lease, source, reading with { ContextTokens = LocalContextTokens, MaxOutputTokens = LocalOutputTokens }),
                    (content, tier) => UnderstandingVerifier.Check(content, text, chunk, profiles, kinds, tier, null) is { } draft
                        ? new LadderCheck<UnderstandingDraft>(draft, draft.Acceptable)
                        : null, cancellationToken);
                input += localOutcome.InputTokens;
                output += localOutcome.OutputTokens;
                // Learn this machine's pace so later drafts are asked for only when they pay off.
                if (localOutcome.IsCompleted)
                {
                    pace?.Observe(chunk.Length, localOutcome.Duration);
                }
                else if (localOutcome.FailureCode == "model_timeout")
                {
                    pace?.ObserveTimeout(chunk.Length, draftPolicy?.Budget ?? TimeSpan.FromSeconds(LocalDraftPolicy.DefaultBudgetSeconds));
                }

                if (local?.Value is { Verified: > 0 } verified)
                {
                    baseline = verified with { Records = verified.Records.Select(record => record with { ProviderId = localOutcome.ProviderId }).ToArray() };
                    localResult = new LadderResult<UnderstandingDraft>(local.Acceptable ? LadderResult<UnderstandingDraft>.Accepted : LadderResult<UnderstandingDraft>.Partial,
                        baseline, ModelTiers.Local, localOutcome.ProviderId, localOutcome.Identity, "local_draft", localOutcome.InputTokens, localOutcome.OutputTokens);
                    steps.Add(new CascadeStep(ModelTiers.Local, localOutcome.ProviderId, baseline.Verified, 0, 0, baseline.Rejected,
                        localOutcome.InputTokens, localOutcome.OutputTokens));
                }
            }

            // 2-3. Strong reviews the draft (or reads), strongest corrects the strong result if it fails.
            UnderstandingDraft? reviewed = baseline;
            DraftDigest? digest = baseline is null ? null : DraftDigest.From(baseline, text);
            Dictionary<string, CascadeStep> tierSteps = new(StringComparer.Ordinal);
            LadderCheck<UnderstandingDraft>? Check(string content, string tier)
            {
                if (reviewed is null || digest is null)
                {
                    UnderstandingDraft? read = UnderstandingVerifier.Check(content, text, chunk, profiles, kinds, tier, null);
                    if (read is not null)
                    {
                        tierSteps[tier] = new CascadeStep(tier, null, 0, 0, read.Verified, read.Rejected, 0, 0);
                    }

                    return read is null ? null : new LadderCheck<UnderstandingDraft>(read, read.Acceptable);
                }

                (UnderstandingDraft Draft, CascadeStep Step, bool Acceptable)? applied =
                    UnderstandingDelta.Apply(content, reviewed, digest, text, chunk, profiles, kinds, tier, null);
                if (applied is null)
                {
                    return null;
                }

                tierSteps[tier] = applied.Value.Step;
                return new LadderCheck<UnderstandingDraft>(applied.Value.Draft, applied.Value.Acceptable);
            }

            TieredRequest Escalate(LadderCheck<UnderstandingDraft> answer)
            {
                reviewed = answer.Value;
                digest = DraftDigest.From(answer.Value, text);
                string[] problems = answer.Value.Rejected > 0
                    ? [$"{answer.Value.Rejected} of the items the previous reviewer added failed verification: the quote was not in the document, the name was not in its quote, or a date or amount was not printed."]
                    : ["The result is incomplete or its summary is missing."];
                return Request(lease, source, UnderstandingPrompt.Review(source.FileName, profile, requested, text, chunk, chunks.Count, digest, "the strong tier", problems));
            }

            TieredRequest first = baseline is null
                ? Request(lease, source, reading)
                : Request(lease, source, UnderstandingPrompt.Review(source.FileName, profile, requested, text, chunk, chunks.Count, digest!, "a small local model", []));
            LadderResult<UnderstandingDraft> result = await ladder.RunAsync(first, Check, Escalate, localResult, cancellationToken);
            input += result.InputTokens;
            output += result.OutputTokens;
            foreach (string tier in new[] { ModelTiers.Strong, ModelTiers.Strongest })
            {
                if (tierSteps.TryGetValue(tier, out CascadeStep? step))
                {
                    steps.Add(step with { ProviderId = result.Tier == tier ? result.ProviderId : null });
                }
            }

            if (result.Value is not { } value)
            {
                stopped = result;
                break;
            }

            partial |= result.Status == LadderResult<UnderstandingDraft>.Partial;
            drafts.Add((value with { Records = value.Records.Select(record => record with { Tier = result.Tier ?? record.Tier }).ToArray() }, result));
        }

        string cascade = Describe(steps, drafts.Sum(item => item.Draft.Verified), drafts.Sum(item => item.Draft.Rejected));
        if (drafts.Count == 0)
        {
            LadderResult<UnderstandingDraft> failed = stopped!;
            return new UnderstandingOutcome(failed.Status, failed.Reason, null, null, [], [], 0, 0, null, failed.ProviderId, null, input, output, complete,
                cascade);
        }

        LadderResult<UnderstandingDraft> strongest = drafts.Select(item => item.Result)
            .OrderByDescending(item => item.Tier == ModelTiers.Strongest).ThenByDescending(item => item.Tier == ModelTiers.Strong).First();
        string? summary = drafts[0].Draft.Summary;
        if (drafts.Count > 1)
        {
            LadderResult<string> combined = await ladder.RunAsync(
                Request(lease, source, UnderstandingPrompt.Combine(source.FileName, drafts.Select(item => item.Draft.Summary ?? string.Empty).ToArray())),
                (content, _) => UnderstandingVerifier.Summary(content), cancellationToken);
            input += combined.InputTokens;
            output += combined.OutputTokens;
            summary = combined.Value ?? summary;
        }

        IReadOnlyList<IntakeRecord> records = drafts
            .SelectMany(item => item.Draft.Records.Select(record => record with { ProviderId = record.ProviderId ?? item.Result.ProviderId }))
            .DistinctBy(record => record.Kind + "\u001f" + string.Join("\u001f", record.Fields.OrderBy(field => field.Key, StringComparer.Ordinal)
                .Select(field => field.Key + "=" + field.Value)))
            .Take(IntakeRecord.MaximumRecordsPerDocument).ToArray();
        bool incomplete = !complete || stopped is not null;
        return new UnderstandingOutcome(
            partial || incomplete ? LadderResult<UnderstandingDraft>.Partial : LadderResult<UnderstandingDraft>.Accepted,
            stopped?.Reason ?? (complete ? "verified" : "document_longer_than_understood"),
            summary, drafts.Select(item => item.Draft.DocumentType).FirstOrDefault(type => type is not null),
            Merge(drafts.SelectMany(item => item.Draft.Candidates)), records,
            drafts.Sum(item => item.Draft.Verified), drafts.Sum(item => item.Draft.Rejected),
            strongest.Tier, strongest.ProviderId, strongest.Identity, input, output, !incomplete, cascade);
    }

    /// <summary>The owner-facing line for a document's cascade, such as "local drafted 12 → strong kept 9, removed 3, added 4 → 13 verified".</summary>
    public static string Describe(IReadOnlyList<CascadeStep> steps, int verified, int rejected)
    {
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        string flow = string.Join(" → ", steps.Select((step, index) => step.Describe(index == 0)));
        string line = $"{flow} → {verified} verified" + (rejected > 0 ? $", {rejected} dropped" : string.Empty);
        return line.Length <= UnderstandingOutcome.MaximumCascadeLength ? line : line[..(UnderstandingOutcome.MaximumCascadeLength - 1)] + "…";
    }

    /// <summary>What makes two candidates the same piece of knowledge, whatever their evidence span.</summary>
    public static string CandidateKey(ProposalCandidate candidate) => string.Join("\u001f", candidate.Kind, KnowledgeKey(candidate.Value),
        candidate.EntityType, KnowledgeKey(candidate.RelationSubject), KnowledgeKey(candidate.RelationObject));

    /// <summary>De-duplicates candidates and keeps the most useful within the per-run bound: topics, then entities, then relations.</summary>
    public static IReadOnlyList<ProposalCandidate> Merge(IEnumerable<ProposalCandidate> candidates)
    {
        ProposalCandidate[] unique = candidates.DistinctBy(CandidateKey).ToArray();
        return unique.Where(candidate => candidate.Kind == "topic").Take(8)
            .Concat(unique.Where(candidate => candidate.Kind == "entity"))
            .Concat(unique.Where(candidate => candidate.Kind == "relation"))
            .Take(ProposalLimits.MaximumPerRun).ToArray();
    }

    private static string? KnowledgeKey(string? value) => Domain.Knowledge.KnowledgeVocabulary.NameKey(value) ?? value;

    private static TieredRequest Request(ProcessingLease lease, LeasedUnderstandingSource source, Inference.ChatRequest request) =>
        new(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, DisclosurePurposes.Intake, request);
}
