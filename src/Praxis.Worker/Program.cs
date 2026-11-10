using Microsoft.Data.Sqlite;
using Praxis.Worker.Intake;
using Praxis.Worker.Recovery;

using CancellationTokenSource cancellation = new();
ConsoleCancelEventHandler stop = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += stop;

bool recovery = args is ["recovery", ..];
try
{
    return recovery
        ? await RecoveryCommand.RunAsync(args, cancellation.Token)
        : await IntakeCommand.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    if (recovery)
    {
        Console.Error.WriteLine("Praxis recovery cancelled.");
        return 130;
    }

    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                      or ArgumentException or NotSupportedException or SqliteException)
{
    Console.Error.WriteLine("Praxis worker failed: " + exception.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= stop;
}
