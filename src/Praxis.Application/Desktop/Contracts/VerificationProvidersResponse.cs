using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Configured verification providers and executables found on the search path.</summary>
public sealed record VerificationProvidersResponse(IReadOnlyList<VerificationProvider> Providers, IReadOnlyDictionary<string, string> Discovered);
