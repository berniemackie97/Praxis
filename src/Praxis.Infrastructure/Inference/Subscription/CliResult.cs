namespace Praxis.Application.Inference;

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
