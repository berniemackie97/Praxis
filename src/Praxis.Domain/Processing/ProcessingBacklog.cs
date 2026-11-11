namespace Praxis.Domain.Processing;

/// <summary>Jobs for current revisions of active sources, grouped by durable processing state.</summary>
public sealed record ProcessingBacklog(long Queued, long Running, long Failed);
