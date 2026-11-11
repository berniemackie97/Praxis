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
catch (Exception exception)
{
    string code = exception switch
    {
        UnauthorizedAccessException => "access_denied",
        DirectoryNotFoundException => "directory_unavailable",
        SqliteException => "storage_error",
        IOException => "io_error",
        InvalidDataException => "invalid_input",
        ArgumentException => "invalid_argument",
        NotSupportedException => "unsupported",
        InvalidOperationException => "invalid_state",
        _ => "unexpected_failure"
    };
    Console.Error.WriteLine("Praxis worker failed (" + code + ").");
    return 1;
}
finally
{
    Console.CancelKeyPress -= stop;
}
