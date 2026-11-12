using Praxis.Application.Inference;
using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>Creates one disposable model session for a configured verifier.</summary>
public interface IVerifierModelFactory
{
    /// <summary>Validates the target and creates a model session without invoking it.</summary>
    ModelConnection Create(VerificationProvider provider);

    /// <summary>Suggested CLI paths. Local Ollama uses an explicit loopback endpoint instead.</summary>
    IReadOnlyDictionary<string, string> DiscoverExecutables();
}
