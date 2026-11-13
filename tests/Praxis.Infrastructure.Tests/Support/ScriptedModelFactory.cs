using Praxis.Application.Inference;
using Praxis.Application.Verification;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;

namespace Praxis.Infrastructure.Tests.Support;

/// <summary>Scripted model sessions per provider, recording every request a provider received.</summary>
internal sealed class ScriptedModelFactory : IVerifierModelFactory
{
    private readonly Dictionary<string, Func<ChatRequest, string>> responders = new(StringComparer.Ordinal);

    public Dictionary<string, List<ChatRequest>> Requests { get; } = new(StringComparer.Ordinal);

    public ScriptedModelFactory Answer(string providerId, Func<ChatRequest, string> respond)
    {
        responders[providerId] = respond;
        return this;
    }

    public ModelConnection Create(VerificationProvider provider)
    {
        if (!responders.TryGetValue(provider.ProviderId, out Func<ChatRequest, string>? respond))
        {
            throw new InferenceException(InferenceFailure.Unavailable, "unscripted provider");
        }

        return new ModelConnection(new ScriptedChatModel(request =>
        {
            if (!Requests.TryGetValue(provider.ProviderId, out List<ChatRequest>? list))
            {
                Requests[provider.ProviderId] = list = [];
            }

            list.Add(request);
            return respond(request);
        })
        {
            Identity = new ModelIdentity(provider.ProviderId, provider.Model ?? "model", "digest", null, null, null)
        });
    }

    public IReadOnlyDictionary<string, string> DiscoverExecutables() => new Dictionary<string, string>();

    public int Count(string providerId) => Requests.TryGetValue(providerId, out List<ChatRequest>? list) ? list.Count : 0;
}
