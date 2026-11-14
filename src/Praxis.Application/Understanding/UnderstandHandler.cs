using Praxis.Application.Documents;
using Praxis.Application.Finance;
using Praxis.Application.Originals;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// The understand stage for every source. Reuses the result of an identical original when one
/// exists; otherwise runs knowledge understanding (summary, topics, entities, relations, records)
/// and, when the document states money movements, the transaction profile, then commits it all in
/// one transaction. Disclosure is decided per call by policy; a held or index-only outcome is
/// recorded with its reason and an owner decision requeues the stage. Overdue holds expire into
/// their fallback whenever the pipeline is idle.
/// </summary>
public sealed class UnderstandHandler(
    IUnderstandingRepository repository, ITextDerivativeRepository texts, IOriginalStore originals, DocumentReaders readers,
    DocumentUnderstander understander, TransactionProfile transactions, IPolicyRepository policy, IProcessingQueue processing,
    TimeProvider clock) : IProcessingHandler
{
    private static readonly HashSet<string> StructuredFormats =
        new(StringComparer.Ordinal) { DocumentFormats.Delimited, DocumentFormats.Spreadsheet, DocumentFormats.FinancialInterchange };

    public string ProcessorKey => ProcessorKeys.IntakeUnderstand;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(20);
    public int MaxConcurrency => 2;

    // Expired holds continue down their fallback chain; files left index-only because no model was
    // set up are understood once one is.
    public bool TryReopenWork() => policy.ExpireHolds(clock.GetUtcNow()) > 0 | repository.RequeueAwaitingModels() > 0;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        LeasedUnderstandingSource? source = repository.ReadLeasedSource(lease);
        LeasedText? input = source is null ? null : texts.ReadLeasedInput(lease);
        if (source is null || input is null || repository.TryReuse(lease, input))
        {
            return;
        }

        // Code is understood per project from a repository map, never one model call per file; the
        // file stays searchable and openable. See ProjectUnderstander.
        if (source.Facts.Format == DocumentFormats.Code)
        {
            repository.CloseUnderstanding(lease, input.Digest, UnderstandingStatuses.IndexOnly, "project_level", 0, 0);
            return;
        }

        DocumentProfile profile = source.Profile ?? FormatHints.Unknown(policy.ReadCategory(source.SourceId)?.Category);
        ReadDocument? structure = StructuredFormats.Contains(source.Facts.Format) ? await ReadStructureAsync(source, cancellationToken) : null;
        DocumentProfile knowledgeProfile = profile with { RecordKinds = profile.RecordKinds.Where(kind => kind != RecordKinds.Transaction).ToArray() };
        UnderstandingOutcome knowledge = await understander.UnderstandAsync(lease, source, input.Content, knowledgeProfile, cancellationToken);
        TransactionOutcome money = TransactionProfile.Applies(profile, structure)
            ? await transactions.ExtractAsync(lease, source, input.Content, structure, cancellationToken)
            : TransactionOutcome.NotApplicable;
        if (processing.Renew(lease, LeaseDuration) is null)
        {
            return;
        }

        int inputTokens = knowledge.InputTokens + money.InputTokens;
        int outputTokens = knowledge.OutputTokens + money.OutputTokens;
        if (!knowledge.HasResult && money.Document is null)
        {
            string status = Closed(money.Status == LadderResult<FinancialReading>.Held ? money.Status : knowledge.Status);
            repository.CloseUnderstanding(lease, input.Digest, status, knowledge.Reason, inputTokens, outputTokens);
            return;
        }

        bool moneyShort = money.Status is not (TransactionOutcome.None or LadderResult<FinancialReading>.Accepted) && money.Document is null;
        string outcome = knowledge.Status == LadderResult<UnderstandingDraft>.Held || money.Status == LadderResult<FinancialReading>.Held
            ? UnderstandingStatuses.Held
            : knowledge.Status == LadderResult<UnderstandingDraft>.Accepted && !moneyShort
                ? UnderstandingStatuses.Understood
                : UnderstandingStatuses.PartiallyVerified;
        string? reason = outcome == UnderstandingStatuses.Understood ? null
            : !knowledge.HasResult ? knowledge.Reason
            : moneyShort ? money.Reason
            : knowledge.Reason;
        repository.CommitUnderstanding(lease, input, new UnderstandingCommit(
            outcome, reason, knowledge.Summary ?? profile.Gist, knowledge.DocumentType ?? profile.DocumentType,
            knowledge.Tier ?? money.Tier, knowledge.ProviderId ?? money.ProviderId, knowledge.Identity ?? money.Identity,
            knowledge.Candidates, knowledge.Records, money.Document, money.Annotations, money.Tier, money.Identity,
            knowledge.Verified + (money.Document?.Transactions.Count ?? 0), knowledge.Rejected, inputTokens, outputTokens, knowledge.Cascade));
    }

    private static string Closed(string ladderStatus) => ladderStatus switch
    {
        LadderResult<UnderstandingDraft>.Held => UnderstandingStatuses.Held,
        LadderResult<UnderstandingDraft>.Skipped => UnderstandingStatuses.Skipped,
        LadderResult<UnderstandingDraft>.Unverified => UnderstandingStatuses.Failed,
        _ => UnderstandingStatuses.IndexOnly
    };

    private async Task<ReadDocument?> ReadStructureAsync(LeasedUnderstandingSource source, CancellationToken cancellationToken)
    {
        byte[] bytes = await originals.ReadVerifiedBytesAsync(source.Original, DocumentLimits.MaximumOriginalBytes, cancellationToken);
        return (await readers.ReadAsync(new DocumentInput(source.FileName, bytes, source.SourceKey), cancellationToken)).Document;
    }
}
