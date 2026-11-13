using Praxis.Domain.Models;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Recorded model calls; remote ones are the disclosure audit.</summary>
public sealed record ModelInvocationsResponse(IReadOnlyList<ModelInvocation> Invocations);
