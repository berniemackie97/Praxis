using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Triages every readable source. Unambiguous formats are triaged by code; otherwise the local
/// tier reads a bounded excerpt (one cheap call, nothing leaves the Mac). Without a usable local
/// answer the source keeps the strictest category and intake continues. Owner-set categories are
/// never overwritten.
/// </summary>
public sealed class TriageHandler(
    IUnderstandingRepository repository, ITextDerivativeRepository texts, TieredModels models, IPolicyRepository policy) : IProcessingHandler
{
    public string ProcessorKey => ProcessorKeys.IntakeTriage;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        LeasedUnderstandingSource? source = repository.ReadLeasedSource(lease);
        LeasedText? input = source is null ? null : texts.ReadLeasedInput(lease);
        if (source is null || input is null)
        {
            return;
        }

        SourceCategory? owner = policy.ReadCategory(source.SourceId) is { Origin: SourceCategory.OwnerOrigin } chosen ? chosen : null;
        DocumentProfile profile;
        string tier;
        if (FormatHints.For(source.Facts) is { } hint)
        {
            (profile, tier) = (hint, "hint");
        }
        else
        {
            TieredOutcome outcome = await models.CompleteAsync(ModelTiers.Local,
                new TieredRequest(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, DisclosurePurposes.Intake,
                    TriagePrompt.Request(source.FileName, source.Facts, input.Content)), cancellationToken);
            (profile, tier) = outcome.IsCompleted && TriagePrompt.Read(outcome.Content!) is { } triaged
                ? (triaged, ModelTiers.Local)
                : (FormatHints.Unknown(), "hint");
        }

        if (owner is not null)
        {
            profile = profile with { Category = owner.Category };
        }
        else if (profile.Category != DataCategories.Unclassified || policy.ReadCategory(source.SourceId) is null)
        {
            policy.SaveCategory(new SourceCategory(source.SourceId, profile.Category,
                tier == ModelTiers.Local ? SourceCategory.ModelOrigin : SourceCategory.HintOrigin, policy.ReadCategory(source.SourceId)?.LocalOnly ?? false));
        }

        repository.CommitTriage(lease, input.Digest, profile, tier);
    }
}
