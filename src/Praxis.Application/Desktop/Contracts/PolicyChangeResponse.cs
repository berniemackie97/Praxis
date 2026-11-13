
namespace Praxis.Application.Desktop.Contracts;

/// <summary>Whether a change applied or awaits confirmation.</summary>
public sealed record PolicyChangeResponse(bool Applied, Guid? PendingChangeId);
