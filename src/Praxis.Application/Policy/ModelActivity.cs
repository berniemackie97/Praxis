using Praxis.Domain.Models;

namespace Praxis.Application.Policy;

/// <summary>
/// The model calls this worker has in flight, so the owner can see what each model is doing and
/// for how long. Kept in memory only; a call leaves the list when it returns, fails or is canceled.
/// </summary>
public sealed class ModelActivity
{
    private readonly Lock gate = new();
    private readonly Dictionary<long, ActiveModelCall> calls = [];
    private long next;

    /// <summary>Marks a call as started; disposing the result marks it finished.</summary>
    public IDisposable Begin(ActiveModelCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        lock (gate)
        {
            long id = ++next;
            calls[id] = call;
            return new Finished(this, id);
        }
    }

    public IReadOnlyList<ActiveModelCall> Snapshot()
    {
        lock (gate)
        {
            return calls.Values.OrderBy(call => call.StartedUtc).ToArray();
        }
    }

    private void End(long id)
    {
        lock (gate)
        {
            calls.Remove(id);
        }
    }

    private sealed class Finished(ModelActivity owner, long id) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.End(id);
            }
        }
    }
}
