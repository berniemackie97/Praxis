using Praxis.Application.Models;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>
/// Everything the Models settings page shows: tools found on this Mac, connected providers with
/// today's use, chat backends, and what each intake tier runs.
/// </summary>
public sealed record ModelSetupResponse(
    IReadOnlyList<ModelTool> Tools, IReadOnlyList<VerificationProvider> Providers, IReadOnlyList<ProviderAllowance> Allowances,
    IReadOnlyList<AgentBackendProfile> Backends, IReadOnlyList<TierAssignment> Tiers);
