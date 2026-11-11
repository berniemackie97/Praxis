namespace Praxis.Domain.Intake;

/// <summary>A bounded, private history of meaningful watched-folder fault transitions.</summary>
public sealed record IntakeIncident(long IncidentId, string RootPath, string State,
    DateTimeOffset StartedUtc, DateTimeOffset? ResolvedUtc);
