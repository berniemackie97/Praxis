using Praxis.Application.Agents;
using Praxis.Domain.Agents;
using Praxis.Domain.Verification;

namespace Praxis.Infrastructure.Agents;

/// <summary>Creates backends from profiles; API keys are read from the secret store per request and never cached.</summary>
public sealed class AgentBackendFactory(HttpClient http, ISecretStore secrets, AgentRelayCommand relay, string socketParent) : IAgentBackendFactory
{
    public IAgentBackend? CreateTurnBackend(AgentBackendProfile profile)
    {
        profile.Validate();
        string? key = profile.KeyAccount is { } account ? secrets.Read(account) ?? throw new InvalidOperationException(
            $"No Keychain item for account '{account}' (service Praxis). Add the API key in Praxis settings.") : null;
        return profile.Kind switch
        {
            // The managed Ollama connection favors visible answers over default model thinking.
            // Other OpenAI-compatible servers do not necessarily support this request field.
            AgentBackendKinds.OpenAiCompatible => new OpenAiCompatibleAgentBackend(http, Base(profile.Endpoint!), profile.Model, key,
                reasoningEffort: profile.BackendId == VerificationProvider.Ollama && profile.IsLocal ? "none" : null),
            AgentBackendKinds.Anthropic => new AnthropicAgentBackend(http, Base(profile.Endpoint!), profile.Model,
                key ?? throw new InvalidOperationException("The Anthropic backend needs an API key in the Keychain.")),
            _ => null
        };
    }

    public ISessionAgentBackend? CreateSessionBackend(AgentBackendProfile profile)
    {
        profile.Validate();
        return profile.Kind switch
        {
            AgentBackendKinds.ClaudeCode => new ClaudeCodeSessionBackend(profile.Executable!, profile.Model, relay, socketParent, profile.Effort),
            AgentBackendKinds.Codex => new CodexSessionBackend(profile.Executable!, profile.Model, relay, socketParent, profile.Effort),
            _ => null
        };
    }

    private static Uri Base(string endpoint) => new(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
}
