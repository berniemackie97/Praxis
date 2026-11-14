namespace Praxis.Domain.Models;

/// <summary>A model call in progress right now: which role asked which provider, about which file, since when.</summary>
public sealed record ActiveModelCall(string Tier, string ProviderId, string? Model, string Purpose, Guid? ItemId, DateTimeOffset StartedUtc);
