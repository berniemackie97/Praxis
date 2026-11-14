using Praxis.Application.Agents;
using Praxis.Application.Policy;
using Praxis.Application.Verification;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;

namespace Praxis.Application.Models;

/// <summary>
/// One place to connect model providers and decide what each does. Connecting Claude Code, Codex
/// or a local Ollama model once makes it available to chat, intake tiers and verification alike;
/// a role then chooses the connection plus the model and reasoning effort for that job.
/// </summary>
public sealed class ModelSetup(
    IVerificationRepository verification, VerificationProviderConfiguration providers, IAgentConversationRepository agents, IPolicyRepository policy)
{
    private const string OllamaChatEndpoint = "/v1";

    /// <summary>Connects (or updates) a plan CLI: the intake/verification provider and the chat backend with the same name.</summary>
    public VerificationProvider ConnectCli(string toolId, string executable, bool enabled, int dailyLimit)
    {
        if (toolId is not (VerificationProvider.ClaudeCode or VerificationProvider.Codex))
        {
            throw new ArgumentException("Only Claude Code and Codex connect through a command-line tool.", nameof(toolId));
        }

        VerificationProvider? existing = verification.ListProviders().FirstOrDefault(provider => provider.ProviderId == toolId);
        VerificationProvider saved = providers.Save(new VerificationProvider(toolId, executable, existing?.Model, existing?.CostWeight ?? DefaultWeight(toolId),
            dailyLimit, enabled, null, Effort: existing?.Effort));
        AgentBackendProfile? backend = agents.ReadBackends().FirstOrDefault(profile => profile.BackendId == toolId);
        agents.SaveBackend(new AgentBackendProfile(toolId, toolId == VerificationProvider.Codex ? AgentBackendKinds.Codex : AgentBackendKinds.ClaudeCode,
            null, backend?.Model ?? "default", null, executable, enabled, backend?.Effort));
        return saved;
    }

    /// <summary>Connects a local Ollama model for the local tier and for private chat.</summary>
    public VerificationProvider ConnectOllama(string endpoint, string model, bool enabled, int dailyLimit)
    {
        VerificationProvider saved = providers.Save(new VerificationProvider(VerificationProvider.Ollama, null, model, 0, dailyLimit, enabled, endpoint));
        agents.SaveBackend(new AgentBackendProfile(VerificationProvider.Ollama, AgentBackendKinds.OpenAiCompatible,
            endpoint.TrimEnd('/') + OllamaChatEndpoint, model, null, null, enabled));
        return saved;
    }

    /// <summary>
    /// Chooses the connection, model and effort for a role. Chat updates that connection's chat
    /// backend; a tier stores its own model and effort so one plan can serve two tiers. A null
    /// connection turns an intake tier off.
    /// </summary>
    public void AssignRole(string role, string? connectionId, string? model, string? effort)
    {
        if (!ModelRoles.All.Contains(role, StringComparer.Ordinal) || !ModelEffort.IsValid(effort) || model?.Length is 0 or > 100)
        {
            throw new ArgumentException("The role assignment is invalid.");
        }

        string? concreteModel = model is null or "default" ? null : model;
        if (role == ModelRoles.Chat)
        {
            AgentBackendProfile backend = agents.ReadBackends().FirstOrDefault(profile => profile.BackendId == connectionId)
                                          ?? throw new ArgumentException("Connect that model before choosing it for chat.", nameof(connectionId));
            agents.SaveBackend(backend with { Model = concreteModel ?? (backend.Kind == AgentBackendKinds.OpenAiCompatible ? backend.Model : "default"), Effort = effort });
            return;
        }

        if (connectionId is not null && verification.ListProviders().All(provider => provider.ProviderId != connectionId))
        {
            throw new ArgumentException("Connect that model before choosing it for intake.", nameof(connectionId));
        }

        policy.SaveTier(role, connectionId, concreteModel, effort);
    }

    private static int DefaultWeight(string toolId) => toolId == VerificationProvider.Codex ? 10 : 50;
}
