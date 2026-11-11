using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Praxis.Worker.Composition;
using Praxis.Worker.Hosting;
using Praxis.Worker.Recovery;

bool recovery = args is ["recovery", ..];
try
{
    return recovery ? await RunRecoveryAsync(args) : await ServeAsync(args);
}
catch (OperationCanceledException) when (recovery)
{
    Console.Error.WriteLine("Praxis recovery cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Praxis worker failed (" + FailureCategory(exception) + ").");
    return 1;
}

static async Task<int> RunRecoveryAsync(string[] args)
{
    using CancellationTokenSource cancellation = new();
    ConsoleCancelEventHandler stop = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += stop;
    try
    {
        return await RecoveryCommand.RunAsync(args, cancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= stop;
    }
}

static async Task<int> ServeAsync(string[] args)
{
    if (!WorkerOptions.TryParse(args, out WorkerOptions? options, out string? usage))
    {
        await Console.Error.WriteLineAsync(usage);
        return 2;
    }

    CatalogDirectoryPreparation.Prepare(options!);
    // Defaults are disabled: configuration never comes from environment variables or files, and
    // the only log sink is the redacting stderr formatter (stdout carries the desktop protocol).
    HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
    builder.Services.Configure<ConsoleLifetimeOptions>(lifetime => lifetime.SuppressStatusMessages = true);
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Logging.AddConsole(console =>
    {
        console.FormatterName = WorkerConsoleFormatter.FormatterName;
        console.LogToStandardErrorThreshold = LogLevel.Trace;
    });
    builder.Logging.AddConsoleFormatter<WorkerConsoleFormatter, ConsoleFormatterOptions>();
    builder.Services.AddPraxisWorker(options!);

    using IHost host = builder.Build();
    WorkerOutcome outcome = host.Services.GetRequiredService<WorkerOutcome>();
    await host.RunAsync();
    if (outcome.Failure is { } failure)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    return 0;
}

static string FailureCategory(Exception exception) => exception switch
{
    UnauthorizedAccessException => "access_denied",
    DirectoryNotFoundException => "directory_unavailable",
    DbException => "storage_error",
    IOException => "io_error",
    InvalidDataException => "invalid_input",
    ArgumentException => "invalid_argument",
    NotSupportedException => "unsupported",
    InvalidOperationException => "invalid_state",
    _ => "unexpected_failure"
};
