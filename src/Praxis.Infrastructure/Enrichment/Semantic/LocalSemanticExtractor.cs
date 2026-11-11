using Praxis.Application.Inference;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Models;
using Praxis.Domain.Processing;
using Praxis.Domain.Text;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Knowledge;
using Praxis.Infrastructure.Processing;

namespace Praxis.Infrastructure.Enrichment.Semantic;

/// <summary>
/// Proposes topics, typed entities and relations for each segment of a revision using the
/// owner's local model, keeping only items whose quoted evidence is found in the segment.
/// Owner decisions about names in a segment (confirmed types, rejected names) are passed as
/// hints. Segments whose text is unchanged since the item's previous result reuse that result
/// instead of calling the model. The model is resolved from the catalog profile per job, so a
/// swap applies without restarting. A disabled or unreachable runtime defers the job instead
/// of failing it; long runs renew their lease and stop when superseded or revoked.
/// </summary>
public sealed class LocalSemanticExtractor(
    LocalCatalog catalog, Func<ModelProfile, ModelConnection> connect) : ILocalProcessingHandler
{
    internal const int MaximumSegments = 64;
    private const int MaximumContextTokens = 4_096;
    private const int MaximumOutputTokens = 1_024;

    public string ProcessorKey => LocalProcessors.SemanticExtraction;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        ModelProfile? profile = catalog.Models.Get(ModelProfile.LocalEnrichment);
        if (profile is not { Enabled: true })
        {
            throw new ProcessorUnavailableException("model_disabled");
        }

        using ModelConnection connection = connect(profile);
        ModelIdentity identity = await DescribeAsync(connection.Model, cancellationToken);
        LeasedText? input = catalog.Text.ReadLeasedInput(lease);
        if (input is null)
        {
            return;
        }

        IReadOnlyList<TextSegmentSpan> segments = catalog.Segments.ReadCompleted(lease.ItemId, lease.Revision);
        Dictionary<string, List<ProposalCandidate>> reusable = ReusableResults(catalog.Proposals.ReadPrior(lease.ItemId, lease.Revision, ProcessorKey));
        IReadOnlyList<EntityHint> hints = catalog.Knowledge.GoverningHints();
        RunState run = new RunState();
        string sourceName = Path.GetFileName(input.Locator);
        foreach (TextSegmentSpan segment in segments.Take(MaximumSegments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = input.Content.Substring(segment.Start, segment.Length);
            if (reusable.TryGetValue(text, out List<ProposalCandidate>? prior))
            {
                run.Reused++;
                foreach (ProposalCandidate candidate in prior)
                {
                    run.TryAdd(candidate with { EvidenceOffset = segment.Start + candidate.EvidenceOffset });
                }

                continue;
            }

            EntityHint[] segmentHints = hints.Where(hint => text.Contains(hint.Name, StringComparison.OrdinalIgnoreCase))
                .Take(SemanticExtractionPrompt.MaximumHints).ToArray();
            await ExtractAsync(connection.Model, profile, sourceName, segment, text, segmentHints, run,
                cancellationToken);
            if (catalog.Processing.Renew(lease, LeaseDuration) is null)
            {
                return; // Lease lost, source revoked or revision superseded: publish nothing.
            }
        }

        if (run.Examined > 0 && run.FailedSegments == run.Examined)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, run.LastFailure!);
            return;
        }

        catalog.Proposals.Commit(lease, input, run.Accepted, new ModelRunRecord(
            identity, run.InputTokens, run.OutputTokens, run.Duration,
            run.Examined + run.Reused, segments.Count, run.Rejected, run.Reused));
    }

    /// <summary>
    /// Earlier candidates grouped by the exact text of the segment they came from, with offsets
    /// relative to that segment. Identical text yields identical evidence spans.
    /// </summary>
    private static Dictionary<string, List<ProposalCandidate>> ReusableResults(PriorExtraction? prior)
    {
        Dictionary<string, List<ProposalCandidate>> reusable = new Dictionary<string, List<ProposalCandidate>>(StringComparer.Ordinal);
        if (prior is null)
        {
            return reusable;
        }

        foreach (TextSegmentSpan segment in prior.Segments)
        {
            string text = prior.Content.Substring(segment.Start, segment.Length);
            List<ProposalCandidate> inside = prior.Candidates
                .Where(candidate => candidate.EvidenceOffset >= segment.Start &&
                    candidate.EvidenceOffset + candidate.EvidenceLength <= segment.Start + segment.Length)
                .Select(candidate => candidate with { EvidenceOffset = candidate.EvidenceOffset - segment.Start })
                .ToList();
            reusable.TryAdd(text, inside);
        }

        return reusable;
    }

    private static async Task<ModelIdentity> DescribeAsync(IChatModel model, CancellationToken cancellationToken)
    {
        try
        {
            return await model.DescribeAsync(cancellationToken);
        }
        catch (InferenceException exception) when (IsRuntimeUnavailable(exception))
        {
            throw new ProcessorUnavailableException(exception.Code, exception);
        }
    }

    private static async Task ExtractAsync(
        IChatModel model, ModelProfile profile, string sourceName, TextSegmentSpan segment,
        string text, IReadOnlyList<EntityHint> hints, RunState run, CancellationToken cancellationToken)
    {
        run.Examined++;
        try
        {
            ChatResult result = await model.CompleteAsync(new ChatRequest(
                SemanticExtractionPrompt.Messages(sourceName, segment.HeadingPath, text, hints),
                SemanticExtractionPrompt.Schema, MaximumOutputTokens,
                Math.Min(profile.ContextTokens, MaximumContextTokens)), cancellationToken);
            run.InputTokens += result.InputTokens;
            run.OutputTokens += result.OutputTokens;
            run.Duration += result.Duration;
            (IReadOnlyList<ProposalCandidate> accepted, int rejected) = SemanticResponseReader.Read(result.Content, text, segment.Start);
            run.Rejected += rejected;
            foreach (ProposalCandidate candidate in accepted)
            {
                run.TryAdd(candidate);
            }
        }
        catch (InferenceException exception) when (IsRuntimeUnavailable(exception))
        {
            throw new ProcessorUnavailableException(exception.Code, exception);
        }
        catch (InferenceException exception)
        {
            // A timed-out, truncated or malformed answer loses only this segment.
            run.FailedSegments++;
            run.LastFailure = exception.Code;
        }
    }

    private static bool IsRuntimeUnavailable(InferenceException exception) => exception.Failure is
        InferenceFailure.Unavailable or InferenceFailure.ModelMissing or InferenceFailure.ServerError;

    private sealed class RunState
    {
        private readonly HashSet<(string Kind, string Value)> seen = [];

        public List<ProposalCandidate> Accepted { get; } = [];
        public int Examined { get; set; }
        public int Reused { get; set; }
        public int FailedSegments { get; set; }
        public string? LastFailure { get; set; }
        public int Rejected { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>Keeps the first evidence for each kind and normalized name (or relation triple).</summary>
        public void TryAdd(ProposalCandidate candidate)
        {
            string identity = candidate.Kind == "relation"
                ? $"{KnowledgeVocabulary.NameKey(candidate.RelationSubject)}|{candidate.Value}|{KnowledgeVocabulary.NameKey(candidate.RelationObject)}"
                : KnowledgeVocabulary.NameKey(candidate.Value) ?? candidate.Value.ToUpperInvariant();
            if (Accepted.Count < LocalProposals.MaximumPerRun && seen.Add((candidate.Kind, identity)))
            {
                Accepted.Add(candidate);
            }
        }
    }
}
