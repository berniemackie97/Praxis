namespace Praxis.Application.Knowledge;

/// <summary>
/// Background loop that keeps the brain's classifications current: once at start it checks for a
/// rule change, then works through entities whose evidence changed in bounded passes and idles
/// when nothing waits. No model calls; every change is a recorded revision.
/// </summary>
public sealed class ReconsolidationRunner(
    IKnowledgeReconsolidation reconsolidation, TimeProvider clock, TimeSpan? idlePoll = null, Action<string>? status = null)
{
    internal const int PassBudget = 200;
    private readonly TimeSpan idle = idlePoll ?? TimeSpan.FromMinutes(1);
    private readonly Action<string> report = status ?? (_ => { });

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            int queued = reconsolidation.EnqueueForRuleChange();
            if (queued > 0)
            {
                report($"Classification rules changed; re-examining {queued} entities.");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                ReconsolidationPass pass = reconsolidation.RunPass(PassBudget);
                if (pass.Applied > 0 || pass.Suggested > 0)
                {
                    report($"Re-examined {pass.Examined} entities: {pass.Applied} corrected, {pass.Suggested} proposed for review, {pass.Remaining} waiting.");
                }

                if (pass.Remaining == 0)
                {
                    await Task.Delay(idle, clock, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
