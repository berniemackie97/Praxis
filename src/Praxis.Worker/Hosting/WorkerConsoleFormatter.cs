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

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string message = logEntry.Formatter(logEntry.State, null);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        textWriter.Write('[');
        textWriter.Write(clock.GetLocalNow().ToString("O"));
        textWriter.Write("] ");
        textWriter.WriteLine(message);
    }
}
