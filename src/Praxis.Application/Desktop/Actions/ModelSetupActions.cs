using Praxis.Application.Agents;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Models;
using Praxis.Application.Policy;
using Praxis.Application.Verification;
using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// The Models settings page: detect installed model tools, connect a provider once for every use,
/// and assign a connection, model and effort to chat and to each intake tier.
/// </summary>
public sealed class ModelSetupActions(
    IModelToolDetector detector, ModelSetup setup, IVerificationRepository verification, IAgentConversationRepository agents, IPolicyRepository policy,
    VerificationRunner? runner = null) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.OfAsync("modelSetup", async (_, cancellationToken) =>
        {
            setup.EnsureChatBackends();
            return Snapshot(await detector.DetectAsync(cancellationToken));
        }),
        DesktopAction.OfAsync("modelToolInspect", async (fields, cancellationToken) => new ModelToolResponse(
            await detector.InspectAsync(fields.RequireString("toolId", 32), fields.RequireString("path", 1024), cancellationToken))),
        DesktopAction.Of("modelConnect", fields =>
        {
            string toolId = fields.RequireString("toolId", 32);
            int limit = fields.IntegerOrDefault("dailyLimit", VerificationProvider.Unlimited, VerificationProvider.Unlimited, VerificationProvider.MaximumDailyLimit);
            bool enabled = fields.RequireBoolean("enabled");
            VerificationProvider saved = toolId == VerificationProvider.Ollama
                ? setup.ConnectOllama(fields.RequireString("endpoint", 256), fields.RequireString("model", 100), enabled, limit)
                : setup.ConnectCli(toolId, fields.RequireString("executable", 1024), enabled, limit);
            runner?.Wake();
            return new VerificationProviderResponse(saved);
        }),
        DesktopAction.Of("modelRoleSet", fields =>
        {
            setup.AssignRole(fields.RequireString("role", 16), fields.OptionalText("connectionId", 64, "The connection is invalid."),
                fields.OptionalText("model", 100, "The model is invalid."), fields.OptionalText("effort", 16, "The effort is invalid."));
            return new ChangedResponse(true);
        })
    ];

    private ModelSetupResponse Snapshot(IReadOnlyList<ModelTool> tools) => new(tools, verification.ListProviders(),
        verification.ListEnabledProviderAllowances(), agents.ReadBackends(), policy.ReadTiers());
}
