using Praxis.Domain.Intake;
using Praxis.Domain.Processing;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Aggregate intake health with per-folder status when several folders are supervised.</summary>
public sealed record IntakeResponse(IntakeStatus Status, IReadOnlyList<WatchedRootStatus>? Roots, ProcessingBacklog? Backlog);
