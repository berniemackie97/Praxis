namespace Praxis.Infrastructure.Intake;

internal sealed record WatchedScanResult(int ObservedFiles, int Imported, TimeSpan? NextScanDelay);
