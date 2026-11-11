using System.Diagnostics;
using System.Text;

using Praxis.Application.Inference;

namespace Praxis.Infrastructure.Inference.Subscription;

/// <summary>
/// Runs a provider CLI as a child process without a shell: arguments are passed as a list,
/// input is written to stdin, output is bounded, and the whole process tree is killed on
/// deadline or cancellation. API-key and endpoint variables are removed from the child's
/// environment so a subscription CLI can never fall back to separately billed API access.
/// </summary>
internal static class CliProcess
{
    internal const int MaximumOutputBytes = 4 * 1024 * 1024;

    private static readonly string[] BillingVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
        "OPENAI_API_KEY", "OPENAI_BASE_URL", "CODEX_API_KEY", "AZURE_OPENAI_API_KEY"
    ];

    internal static async Task<CliResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? stdin, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var variable in BillingVariables)
        {
            start.Environment.Remove(variable);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new InferenceException(InferenceFailure.Unavailable,
                "The provider command could not be started.", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), deadline.Token);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new CliResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException exception)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new InferenceException(InferenceFailure.Timeout,
                "The provider command exceeded its time budget.", exception);
        }
        catch (InferenceException)
        {
            Kill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (builder.Length + read > MaximumOutputBytes)
            {
                throw new InferenceException(InferenceFailure.InvalidResponse,
                    "The provider command produced more output than allowed.");
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
    }
}
