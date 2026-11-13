using Praxis.Application.Inference;
using Praxis.Domain.Verification;

namespace Praxis.Application.Policy;

/// <summary>An open model session for one tier and the provider that serves it.</summary>
public sealed class TierSession(string tier, VerificationProvider provider, ModelConnection connection) : IDisposable
{
    public string Tier { get; } = tier;
    public VerificationProvider Provider { get; } = provider;
    public IChatModel Model => connection.Model;

    public void Dispose() => connection.Dispose();
}
