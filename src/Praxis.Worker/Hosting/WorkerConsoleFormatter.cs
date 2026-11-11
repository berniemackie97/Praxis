using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Praxis.Worker.Hosting;

/// <summary>
/// Writes one timestamped operational line per event to stderr. Exception details are never
/// written: they can carry private paths or provider text, which must stay out of diagnostics.
/// </summary>
internal sealed class WorkerConsoleFormatter(TimeProvider clock) : ConsoleFormatter(FormatterName)
{
    internal const string FormatterName = "praxis-worker";
    private readonly string runId = Guid.NewGuid().ToString("N");
    private long sequence;

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string message = logEntry.Formatter(logEntry.State, null);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        textWriter.Write('[');
        textWriter.Write(clock.GetUtcNow().ToString("O"));
        textWriter.Write("] run=");
        textWriter.Write(runId);
        textWriter.Write(" seq=");
        textWriter.Write(Interlocked.Increment(ref sequence));
        textWriter.Write(' ');
        textWriter.WriteLine(message);
    }
}
