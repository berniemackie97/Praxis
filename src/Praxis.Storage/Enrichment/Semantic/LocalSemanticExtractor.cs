using Praxis.Inference;

namespace Praxis.Storage;

/// <summary>
/// Proposes topics and typed entities for each segment of a revision using the owner's local
/// model, keeping only items whose quoted evidence is found in the segment. The model is
/// resolved from the catalog profile per job, so a swap applies without restarting. A disabled
/// or unreachable runtime defers the job instead of failing it; long runs renew their lease
/// and stop when the revision is superseded or the source revoked.
/// </summary>
public sealed class LocalSemanticExtractor(
    LocalCatalog catalog, Func<ModelProfile, ModelConnection> connect) : ILocalProcessingHandler
{
    internal const int MaximumSegments = 64;
    private const int MaximumContextTokens = 4_096;
    private const int MaximumOutputTokens = 768;

    public string ProcessorKey => LocalProcessors.SemanticExtraction;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        var profile = catalog.Models.Get(ModelProfile.LocalEnrichment);
        if (profile is not { Enabled: true })
        {
            throw new ProcessorUnavailableException("model_disabled");
        }

        using var connection = connect(profile);
        var identity = await DescribeAsync(connection.Model, cancellationToken);
        var input = catalog.Text.ReadLeasedInput(lease);
        if (input is null)
        {
            return;
        }

        var segments = catalog.Segments.ReadCompleted(lease.ItemId, lease.Revision);
        var run = new RunState();
        var sourceName = Path.GetFileName(input.Locator);
        foreach (var segment in segments.Take(MaximumSegments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = input.Content.Substring(segment.Start, segment.Length);
            await ExtractAsync(connection.Model, profile, sourceName, segment, text, run, cancellationToken);
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
            run.Examined, segments.Count, run.Rejected));
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
        string text, RunState run, CancellationToken cancellationToken)
    {
        run.Examined++;
        try
        {
            var result = await model.CompleteAsync(new ChatRequest(
                SemanticExtractionPrompt.Messages(sourceName, segment.HeadingPath, text),
                SemanticExtractionPrompt.Schema, MaximumOutputTokens,
                Math.Min(profile.ContextTokens, MaximumContextTokens)), cancellationToken);
            run.InputTokens += result.InputTokens;
            run.OutputTokens += result.OutputTokens;
            run.Duration += result.Duration;
            var (accepted, rejected) = SemanticResponseReader.Read(result.Content, text, segment.Start);
            run.Rejected += rejected;
            foreach (var candidate in accepted)
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
        public int FailedSegments { get; set; }
        public string? LastFailure { get; set; }
        public int Rejected { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>Keeps the first evidence for each kind and case-insensitive name.</summary>
        public void TryAdd(ProposalCandidate candidate)
        {
            if (Accepted.Count < LocalProposals.MaximumPerRun &&
                seen.Add((candidate.Kind, candidate.Value.ToUpperInvariant())))
            {
                Accepted.Add(candidate);
            }
        }
    }
}
