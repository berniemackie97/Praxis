using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Application.Policy;
using Praxis.Application.Verification;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Inference;
using Praxis.Infrastructure.Inference.Subscription;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Models;

/// <summary>Connect a provider once, find it automatically, and give each role its own model and effort.</summary>
public sealed class ModelSetupTests : IDisposable
{
    private const string Catalog = """
        {"models":[
          {"slug":"gpt-6-sol","display_name":"GPT-6-Sol","visibility":"list","default_reasoning_level":"medium",
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"}]},
          {"slug":"internal-review","display_name":"Internal","visibility":"hide","supported_reasoning_levels":[]}]}
        """;

    private readonly string folder = Path.Combine(TestPaths.Temp, "praxis-models-" + Guid.NewGuid().ToString("N"));
    private readonly string data;

    public ModelSetupTests()
    {
        data = Path.Combine(folder, "data");
        Directory.CreateDirectory(data);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task InstalledPlanToolsAreFoundWithVersionSignInAndModelCatalog()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Script("claude", """
            case "$1" in
              --version) echo '2.1.322 (Claude Code)' ;;
              auth) echo '{"loggedIn":true,"authMethod":"claude.ai"}' ;;
            esac
            """);
        Script("codex", $$"""
            case "$1" in
              --version) echo 'codex-cli 0.167.0' ;;
              login) echo 'Logged in using ChatGPT' ;;
              debug) cat <<'JSON'
            {{Catalog}}
            JSON
              ;;
            esac
            """);
        ModelToolDetector detector = new(command => [Path.Combine(folder, command), Path.Combine(folder, "missing", command)]);

        IReadOnlyList<ModelTool> tools = await detector.DetectAsync(CancellationToken.None);

        ModelTool claude = Assert.Single(tools, tool => tool.Id == ModelTool.ClaudeCode);
        Assert.Equal(("2.1.322 (Claude Code)", true, "Claude plan", null), (claude.Version, claude.SignedIn, claude.Account, claude.Problem));
        Assert.Contains(claude.Models, model => model.Id == "opus" && model.Efforts.Contains("max"));
        ModelTool codex = Assert.Single(tools, tool => tool.Id == ModelTool.Codex);
        Assert.Equal(("codex-cli 0.167.0", true, "ChatGPT plan"), (codex.Version, codex.SignedIn, codex.Account));
        ModelChoice sol = Assert.Single(codex.Models, model => model.Id == "gpt-6-sol");
        Assert.Equal(["low", "medium", "high", "xhigh"], sol.Efforts);
        Assert.Equal("medium", sol.DefaultEffort);
        Assert.DoesNotContain(codex.Models, model => model.Id == "internal-review");
        Assert.Equal("not_found", Assert.Single(tools, tool => tool.Id == ModelTool.Ollama).Problem);

        Script("claude", """
            case "$1" in
              --version) echo '2.1.322 (Claude Code)' ;;
              auth) echo '{"loggedIn":false}' ;;
            esac
            """);
        ModelTool signedOut = await detector.InspectAsync(ModelTool.ClaudeCode, Path.Combine(folder, "claude"), CancellationToken.None);
        Assert.Equal((false, "signed_out"), (signedOut.SignedIn, signedOut.Problem));
        Assert.Equal("not_found", (await detector.InspectAsync(ModelTool.Codex, "/nonexistent/codex", CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task ConnectingOnceServesChatAndIntakeAndEachTierRunsItsOwnModelAndEffort()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        List<VerificationProvider> created = [];
        DelegatingVerifierModelFactory factory = new(provider =>
        {
            created.Add(provider);
            return new ScriptedChatModel(_ => """{"ok":true}""");
        });
        ModelSetup setup = new(catalog.Verification, new VerificationProviderConfiguration(catalog.Verification, factory), catalog.Agents, catalog.Policy);
        string claude = Path.Combine(folder, "claude");
        File.WriteAllText(claude, "#!/bin/sh\n");

        setup.ConnectCli(VerificationProvider.ClaudeCode, claude, enabled: true, VerificationProvider.Unlimited);
        setup.AssignRole(ModelTiers.Strong, VerificationProvider.ClaudeCode, "sonnet", "medium");
        setup.AssignRole(ModelTiers.Strongest, VerificationProvider.ClaudeCode, "opus", "high");
        setup.AssignRole(ModelRoles.Chat, VerificationProvider.ClaudeCode, "opus", "max");

        VerificationProvider provider = Assert.Single(catalog.Verification.ListProviders());
        Assert.Equal((VerificationProvider.Unlimited, true), (provider.DailyLimit, provider.WithinLimit(1_000_000)));
        AgentBackendProfile chat = Assert.Single(catalog.Agents.ReadBackends());
        Assert.Equal((AgentBackendKinds.ClaudeCode, claude, "opus", "max"), (chat.Kind, chat.Executable, chat.Model, chat.Effort));
        Assert.Equal([("strong", "sonnet", "medium"), ("strongest", "opus", "high")],
            catalog.Policy.ReadTiers().Select(tier => (tier.Tier, tier.Model!, tier.Effort!)));

        new PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Documents,
            VerificationProvider.ClaudeCode, DisclosurePurposes.Intake, true, null)), PolicyActors.Owner);
        TieredModels tiers = new(catalog.Policy, catalog.Verification, factory, new DisclosureGate(catalog.Policy, TimeProvider.System), TimeProvider.System);
        ChatRequest request = new([new ChatMessage(ChatRole.User, "x")], JsonDocument.Parse("""{"type":"object"}""").RootElement, 64, 2_048);
        created.Clear();
        await tiers.CompleteAsync(ModelTiers.Strongest, new TieredRequest(null, null, null, null, DisclosurePurposes.Intake, request, DataCategories.Documents),
            CancellationToken.None);
        Assert.Equal(("opus", "high"), (Assert.Single(created).Model, created[0].Effort));

        setup.AssignRole(ModelTiers.Strongest, null, null, null);
        Assert.False(tiers.IsConfigured(ModelTiers.Strongest));
    }

    [Fact]
    public async Task PlanCommandsReceiveTheChosenEffortAndPlanDefaultSkipsTheModelFlag()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string record = Path.Combine(folder, "args.txt");
        string claude = Script("claude-run", $$$"""
            printf '%s\n' "$@" > '{{{record}}}'
            cat > /dev/null
            echo '{"type":"result","subtype":"success","is_error":false,"structured_output":{"ok":true},"usage":{"input_tokens":1,"output_tokens":1}}'
            """);
        ChatRequest request = new([new ChatMessage(ChatRole.System, "s"), new ChatMessage(ChatRole.User, "u")],
            JsonDocument.Parse("""{"type":"object"}""").RootElement, 64, 2_048);

        await new ClaudeCodeChatModel(claude, "default", effort: "xhigh").CompleteAsync(request, CancellationToken.None);
        string[] lines = await File.ReadAllLinesAsync(record);
        Assert.DoesNotContain("--model", lines);
        Assert.Equal("xhigh", lines[Array.IndexOf(lines, "--effort") + 1]);
        Assert.Throws<ArgumentException>(() => new ClaudeCodeChatModel(claude, "sonnet", effort: "high; rm"));
    }

    private string Script(string name, string body)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
