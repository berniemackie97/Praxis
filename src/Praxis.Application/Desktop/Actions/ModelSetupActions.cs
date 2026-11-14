using Praxis.Application.Agents;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Application.Verification;
using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// The Models settings page: detect installed model tools, connect a provider once for every use,
/// and assign a connection, model and effort to chat and to each intake tier.
/// </summary>
public sealed class ModelSetupActions(
    IModelToolDetector detector, ModelSetup setup, IVerificationRepository verification, IAgentConversationRepository agents, IPolicyRepository policy,
    VerificationRunner? runner = null, ProcessingPipeline? pipeline = null, ILocalModelDirectory? localModels = null, ISettingsStore? settings = null,
    LocalPace? pace = null) : IDesktopActionGroup
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
                ? setup.ConnectOllama(fields.RequireString("endpoint", 256), fields.RequireString("model", 100), enabled)
                : setup.ConnectCli(toolId, fields.RequireString("executable", 1024), enabled, limit);
            runner?.Wake();
            // A new connection can make waiting files understandable now.
            pipeline?.Wake();
            return new VerificationProviderResponse(saved);
        }),
        DesktopAction.OfAsync("modelMachine", async (_, cancellationToken) => await MachineAsync(cancellationToken)),
        DesktopAction.OfAsync("modelMachineApply", async (_, cancellationToken) =>
        {
            MachineResponse machine = await MachineAsync(cancellationToken);
            setup.Apply(machine.Recommended, OllamaEndpoint());
            pipeline?.Wake();
            return await MachineAsync(cancellationToken);
        }),
        DesktopAction.Of("localModelSet", fields =>
        {
            setup.UseLocalModel(fields.RequireString("model", 200), OllamaEndpoint());
            pipeline?.Wake();
            return new ChangedResponse(true);
        }),
        DesktopAction.Of("localDraftSet", fields =>
        {
            string mode = fields.RequireString("mode", 16);
            if (!LocalDraftModes.IsValid(mode) || settings is null)
            {
                throw new ArgumentException("Choose auto, always or off.");
            }

            settings.Write(LocalDraftPolicy.ModeKey, mode);
            if (fields.OptionalInteger("budgetSeconds", 5, 600) is { } budget)
            {
                settings.Write(LocalDraftPolicy.BudgetKey, budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return new ChangedResponse(true);
        }),
        DesktopAction.Of("modelRoleSet", fields =>
        {
            setup.AssignRole(fields.RequireString("role", 16), fields.OptionalText("connectionId", 64, "The connection is invalid."),
                fields.OptionalText("model", 100, "The model is invalid."), fields.OptionalText("effort", 16, "The effort is invalid."));
            pipeline?.Wake();
            return new ChangedResponse(true);
        })
    ];

    private async Task<MachineResponse> MachineAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Domain.Models.InstalledModel> installed = [];
        if (localModels is not null)
        {
            try
            {
                installed = await localModels.ListInstalledAsync(OllamaEndpoint(), cancellationToken);
            }
            catch (Inference.InferenceException)
            {
                // Ollama not running: recommend from what is known; the page says so.
            }
        }

        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        LocalDraftPolicy policy = new(settings ?? new NoSettings(), pace ?? new LocalPace());
        return new MachineResponse(MachineTuning.Plan(memory, installed), policy.Mode, (int)policy.Budget.TotalSeconds,
            pace?.SecondsPerThousandCharacters, installed);
    }

    private string OllamaEndpoint() =>
        verification.ListProviders().FirstOrDefault(provider => provider.ProviderId == VerificationProvider.Ollama)?.Endpoint ?? "http://127.0.0.1:11434";

    private sealed class NoSettings : ISettingsStore
    {
        public string? Read(string key) => null;

        public void Write(string key, string value)
        {
        }
    }

    private ModelSetupResponse Snapshot(IReadOnlyList<ModelTool> tools) => new(tools, verification.ListProviders(),
        verification.ListEnabledProviderAllowances(), agents.ReadBackends(), policy.ReadTiers());
}
