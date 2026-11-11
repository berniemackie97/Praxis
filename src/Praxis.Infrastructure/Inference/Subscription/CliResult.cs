namespace Praxis.Infrastructure.Inference.Subscription;

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
