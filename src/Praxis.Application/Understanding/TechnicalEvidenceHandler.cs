using Praxis.Application.Enrichment;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Domain.Processing;

namespace Praxis.Application.Understanding;

/// <summary>Extracts technical evidence without a model. Publication uses the normal revision and lease fence.</summary>
public sealed class TechnicalEvidenceHandler(ITextDerivativeRepository text, IProposalRepository proposals) : IProcessingHandler
{
    public string ProcessorKey => ProcessorKeys.TechnicalEvidence;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(2);
    public int MaxConcurrency => 1;
    public bool TryReopenWork() => false;

    public Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.ReadLeasedInput(lease) is { } input)
        {
            proposals.Commit(lease, input, TechnicalEvidence.Extract(input.Locator, input.Content));
        }

        return Task.CompletedTask;
    }
}
