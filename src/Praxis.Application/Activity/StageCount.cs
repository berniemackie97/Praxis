namespace Praxis.Application.Activity;

/// <summary>How many jobs of one processing stage wait, run, failed, and finished today (UTC).</summary>
public sealed record StageCount(string Stage, int Queued, int Running, int Failed, int DoneToday);
