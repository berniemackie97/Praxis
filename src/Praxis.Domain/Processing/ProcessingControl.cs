namespace Praxis.Domain.Processing;

public sealed record ProcessingControl(string ProcessorKey, bool Paused, string State, int Running, int Waiting, string? Actor, DateTimeOffset? ChangedUtc);
