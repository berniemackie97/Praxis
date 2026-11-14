using Praxis.Application.Models;
using Praxis.Domain.Models;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>
/// This machine's recommended local setup, the current draft settings, the local tier's measured
/// pace (seconds per thousand characters, once known) and the installed local models.
/// </summary>
public sealed record MachineResponse(
    MachinePlan Recommended, string DraftMode, int DraftBudgetSeconds, double? SecondsPerThousandCharacters, IReadOnlyList<InstalledModel> Installed);
