using Praxis.Application.Inference;
using Praxis.Application.Verification;
using Praxis.Domain.Verification;

namespace Praxis.Infrastructure.Tests.Support;

/// <summary>Serves scripted verifier models without touching provider executables.</summary>
internal sealed class DelegatingVerifierModelFactory(Func<VerificationProvider, IChatModel> create) : IVerifierModelFactory
{
    public IChatModel Create(VerificationProvider provider) => create(provider);

    public IReadOnlyDictionary<string, string> DiscoverExecutables() => new Dictionary<string, string>();
}
