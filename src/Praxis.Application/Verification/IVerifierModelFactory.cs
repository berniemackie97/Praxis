using Praxis.Application.Inference;
using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>Creates the model adapter behind an owner-configured verification provider.</summary>
public interface IVerifierModelFactory
{
    /// <summary>Creates the adapter, validating the provider's executable path and model name.</summary>
    IChatModel Create(VerificationProvider provider);

    /// <summary>
    /// Absolute paths of provider executables found on the worker's search path, offered to the
    /// owner as suggestions. Nothing is enabled automatically.
    /// </summary>
    IReadOnlyDictionary<string, string> DiscoverExecutables();
}
