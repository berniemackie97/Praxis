namespace Praxis.Application.Desktop.Contracts;

/// <summary>A model call running now, with the file it is about when it is about one.</summary>
public sealed record ModelCallInProgress(string Tier, string ProviderId, string? Model, string Purpose, string? FileName, DateTimeOffset StartedUtc);
