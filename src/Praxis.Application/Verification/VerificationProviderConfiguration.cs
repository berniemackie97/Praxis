using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>Validates and stores an owner-configured verification provider.</summary>
public sealed class VerificationProviderConfiguration(IVerificationRepository repository, IVerifierModelFactory models)
{
    public VerificationProvider Save(VerificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.EnsureValid();
        // Constructing the adapter validates the executable path and model name.
        _ = models.Create(provider);
        repository.SaveProvider(provider);
        return provider;
    }
}
