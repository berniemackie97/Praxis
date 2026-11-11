namespace Praxis.Domain.Intake;

public sealed record ObservedFile(string SourcePath, long Length, DateTime LastWriteUtc);
