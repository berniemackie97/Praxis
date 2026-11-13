using Praxis.Application.Policy;
using Praxis.Application.Understanding.Profiles;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Understands a document's text on the strong tier, one call per span, with code verification
/// per item and the strongest tier asked when a span's answer is not acceptable. Spans are merged
/// in code (duplicates removed, proposals bounded by priority); a long document's span summaries
/// are combined in one small call.
/// </summary>
public sealed class DocumentUnderstander(IntakeLadder ladder, RecordProfiles profiles)
{
    public async Task<UnderstandingOutcome> UnderstandAsync(
        ProcessingLease lease, LeasedUnderstandingSource source, string text, DocumentProfile profile, CancellationToken cancellationToken)
    {
        IReadOnlyList<IRecordProfile> requested = profiles.Requested(profile.RecordKinds);
        string[] kinds = requested.Select(record => record.Kind).ToArray();
        (IReadOnlyList<DocumentChunk> chunks, bool complete) = DocumentChunker.Split(text);
        List<(UnderstandingDraft Draft, LadderResult<UnderstandingDraft> Result)> drafts = [];
        int input = 0;
        int output = 0;
        bool partial = false;
        LadderResult<UnderstandingDraft>? stopped = null;
        foreach (DocumentChunk chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TieredRequest request = Request(lease, source, UnderstandingPrompt.Request(source.FileName, profile, requested, text, chunk, chunks.Count));
            LadderResult<UnderstandingDraft> result = await ladder.RunAsync(request, (content, tier) =>
                UnderstandingVerifier.Check(content, text, chunk, profiles, kinds, tier, null) is { } draft
                    ? new LadderCheck<UnderstandingDraft>(draft, draft.Acceptable)
                    : null, cancellationToken);
            input += result.InputTokens;
            output += result.OutputTokens;
            if (result.Value is not { } value)
            {
                stopped = result;
                break;
            }

            partial |= result.Status == LadderResult<UnderstandingDraft>.Partial;
            drafts.Add((value, result));
        }

        if (drafts.Count == 0)
        {
            LadderResult<UnderstandingDraft> failed = stopped!;
            return new UnderstandingOutcome(failed.Status, failed.Reason, null, null, [], [], 0, 0, null, failed.ProviderId, null, input, output, complete);
        }

        LadderResult<UnderstandingDraft> strongest = drafts.Select(item => item.Result)
            .OrderByDescending(item => item.Tier == ModelTiers.Strongest).First();
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
            .SelectMany(item => item.Draft.Records.Select(record => record with { ProviderId = item.Result.ProviderId }))
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
            strongest.Tier, strongest.ProviderId, strongest.Identity, input, output, !incomplete);
    }

    /// <summary>De-duplicates candidates and keeps the most useful within the per-run bound: topics, then entities, then relations.</summary>
    public static IReadOnlyList<ProposalCandidate> Merge(IEnumerable<ProposalCandidate> candidates)
    {
        ProposalCandidate[] unique = candidates
            .DistinctBy(candidate => string.Join("\u001f", candidate.Kind, KnowledgeKey(candidate.Value), candidate.EntityType,
                KnowledgeKey(candidate.RelationSubject), KnowledgeKey(candidate.RelationObject)))
            .ToArray();
        return unique.Where(candidate => candidate.Kind == "topic").Take(8)
            .Concat(unique.Where(candidate => candidate.Kind == "entity"))
            .Concat(unique.Where(candidate => candidate.Kind == "relation"))
            .Take(ProposalLimits.MaximumPerRun).ToArray();
    }

    private static string? KnowledgeKey(string? value) => Domain.Knowledge.KnowledgeVocabulary.NameKey(value) ?? value;

    private static TieredRequest Request(ProcessingLease lease, LeasedUnderstandingSource source, Inference.ChatRequest request) =>
        new(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, DisclosurePurposes.Intake, request);
}
