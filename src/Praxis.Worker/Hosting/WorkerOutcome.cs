namespace Praxis.Worker.Hosting;

/// <summary>How the supervised worker services ended, read after the host stops.</summary>
internal sealed class WorkerOutcome
{
    public Exception? Failure { get; private set; }

    public void Fail(Exception failure) => Failure ??= failure;
}
