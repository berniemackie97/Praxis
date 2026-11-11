namespace Praxis.Domain.Intake;

public sealed record IntakeStatus(string State, bool Paused, DateTimeOffset? LastSuccessfulScanUtc, int ObservedFiles, int ImportedSinceStart, int ErrorCount, IReadOnlyList<IntakeFileError> FileErrors, string? Problem);
