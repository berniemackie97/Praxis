using Praxis.Application.Finance.Categorization;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Domain.Finance;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;

namespace Praxis.Application.Finance;

/// <summary>
/// Understanding stage after rows are recorded: merchant and category for every row, from the
/// strong tier (or the category's fallback chain), validated against vocabulary and direction
/// and stored with tier provenance. Provider facts are never changed. An owner hold closes the
/// job; the owner's decision requeues it.
/// </summary>
public sealed class FinancialAnnotationHandler(IFinancialLedgerRepository ledger, IntakeLadder ladder, IProcessingQueue processing) : IProcessingHandler
{
    private const int OutputTokens = 4_096;
    private const int RequestContextTokens = 16_384;

    public string ProcessorKey => ProcessorKeys.FinancialCategories;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(15);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        LeasedFinancialRows? leased = ledger.ReadLeasedTransactions(lease);
        if (leased is null)
        {
            return;
        }

        List<TransactionAnnotation> annotations = [];
        foreach (ParsedTransaction[] chunk in leased.Rows.Chunk(AnnotationPrompt.MaximumRows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TieredRequest request = new(leased.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, DisclosurePurposes.Intake,
                new ChatRequest(AnnotationPrompt.Messages(chunk), AnnotationPrompt.Schema, OutputTokens, RequestContextTokens), DataCategories.Financial);
            LadderResult<IReadOnlyList<(int Ordinal, string Merchant, string Category)>> result = await ladder.RunAsync(request,
                (content, _) => AnnotationReader.Read(content, chunk) is { Count: > 0 } accepted ? accepted : null, cancellationToken);
            if (result.Status != LadderResult<IReadOnlyList<(int, string, string)>>.Accepted)
            {
                break;
            }

            annotations.AddRange(result.Value!.Select(item =>
                new TransactionAnnotation(item.Ordinal, item.Merchant, item.Category, result.Tier!, result.ProviderId!, result.Identity?.Model)));
            if (processing.Renew(lease, LeaseDuration) is null)
            {
                return;
            }
        }

        ledger.CommitAnnotations(lease, annotations);
    }
}
