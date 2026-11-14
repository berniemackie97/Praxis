namespace Praxis.Application.Activity;

/// <summary>A processing job a worker holds now: the file, the stage, which attempt, when it started and until when its lease runs.</summary>
public sealed record RunningJob(Guid ItemId, string FileName, string Stage, int Attempt, DateTimeOffset StartedUtc, DateTimeOffset? LeaseUntilUtc);
