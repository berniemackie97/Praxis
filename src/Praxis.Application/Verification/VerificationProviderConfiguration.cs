using Praxis.Application.Inference;
using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>Validates and stores an owner-configured verification provider.</summary>
public sealed class VerificationProviderConfiguration(IVerificationRepository repository, IVerifierModelFactory models)
{
    public VerificationProvider Save(VerificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.EnsureValid();
        // Disabling a provider must remain possible even if its CLI was uninstalled.
        if (provider.Enabled)
        {
            using ModelConnection connection = models.Create(provider);
        }

        repository.SaveProvider(provider);
        return repository.ListProviders().Single(saved => saved.ProviderId == provider.ProviderId);
    }
}
