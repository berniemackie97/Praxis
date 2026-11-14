using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Inference;

/// <summary>
/// Finds Claude Code, Codex and Ollama where installers put them (Homebrew, the native installers,
/// npm, bun, Volta and nvm), not only on the worker's minimal PATH, and asks each for its version,
/// sign-in state and model catalog. Every probe runs without a shell, in an empty private folder,
/// within a few seconds; nothing is sent anywhere and no output except the parsed fields is kept.
/// </summary>
public sealed class ModelToolDetector : IModelToolDetector
{
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(10);

    // Declared before the models that use it: static fields initialize in order.
    private static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];

    private static readonly ModelChoice[] ClaudeModels =
    [
        Claude("default", "Plan default"), Claude("fable", "Fable (most capable)"), Claude("opus", "Opus"),
        Claude("sonnet", "Sonnet (balanced)"), Claude("haiku", "Haiku (fastest)")
    ];

    private readonly Func<string, IEnumerable<string>> candidates;

    public ModelToolDetector() : this(DefaultCandidates)
    {
    }

    internal ModelToolDetector(Func<string, IEnumerable<string>> candidates) => this.candidates = candidates;

    public async Task<IReadOnlyList<ModelTool>> DetectAsync(CancellationToken cancellationToken)
    {
        Task<ModelTool>[] probes =
        [
            Find(ModelTool.ClaudeCode, "claude", cancellationToken),
            Find(ModelTool.Codex, "codex", cancellationToken),
            Find(ModelTool.Ollama, "ollama", cancellationToken)
        ];
        return await Task.WhenAll(probes);
    }

    public Task<ModelTool> InspectAsync(string toolId, string path, CancellationToken cancellationToken)
    {
        if (toolId is not (ModelTool.ClaudeCode or ModelTool.Codex or ModelTool.Ollama))
        {
            throw new ArgumentException("The model tool is not supported.", nameof(toolId));
        }

        return !Path.IsPathFullyQualified(path) || !File.Exists(path)
            ? Task.FromResult(new ModelTool(toolId, path, null, false, null, "not_found", []))
            : ProbeAsync(toolId, path, cancellationToken);
    }

    /// <summary>Where installers put a command: PATH, Homebrew, the native installers, npm, bun, Volta and nvm.</summary>
    internal static IEnumerable<string> DefaultCandidates(string command)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified);
        string[] known =
        [
            "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"), Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".volta", "bin"),
            Path.Combine(home, "Library", "pnpm"), Path.Combine(home, ".cargo", "bin"), "/Applications/Ollama.app/Contents/Resources"
        ];
        string nvm = Path.Combine(home, ".nvm", "versions", "node");
        IEnumerable<string> node = Directory.Exists(nvm)
            ? Directory.EnumerateDirectories(nvm).OrderByDescending(version => version, StringComparer.Ordinal).Select(version => Path.Combine(version, "bin"))
            : [];
        return path.Concat(known).Concat(node).Distinct(StringComparer.Ordinal).Select(directory => Path.Combine(directory, command));
    }

    private async Task<ModelTool> Find(string toolId, string command, CancellationToken cancellationToken)
    {
        string? path = candidates(command).FirstOrDefault(File.Exists);
        return path is null ? new ModelTool(toolId, null, null, false, null, "not_found", []) : await ProbeAsync(toolId, path, cancellationToken);
    }

    private static async Task<ModelTool> ProbeAsync(string toolId, string path, CancellationToken cancellationToken)
    {
        try
        {
            return toolId switch
            {
                ModelTool.ClaudeCode => await ClaudeAsync(path, cancellationToken),
                ModelTool.Codex => await CodexAsync(path, cancellationToken),
                _ => await OllamaAsync(path, cancellationToken)
            };
        }
        catch (InferenceException)
        {
            return new ModelTool(toolId, path, null, false, null, "not_responding", []);
        }
    }

    private static async Task<ModelTool> ClaudeAsync(string path, CancellationToken cancellationToken)
    {
        string? version = await VersionAsync(path, cancellationToken);
        CliResult status = await RunAsync(path, ["auth", "status"], cancellationToken);
        bool signedIn = false;
        string? account = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(status.Stdout);
            signedIn = document.RootElement.TryGetProperty("loggedIn", out JsonElement loggedIn) && loggedIn.ValueKind == JsonValueKind.True;
            account = document.RootElement.TryGetProperty("authMethod", out JsonElement method) ? method.GetString() : null;
        }
        catch (JsonException)
        {
            // Older versions print text; treat a zero exit as signed in.
            signedIn = status.ExitCode == 0;
        }

        return new ModelTool(ModelTool.ClaudeCode, path, version, signedIn, account is "claude.ai" ? "Claude plan" : account,
            signedIn ? null : "signed_out", ClaudeModels);
    }

    private static async Task<ModelTool> CodexAsync(string path, CancellationToken cancellationToken)
    {
        string? version = await VersionAsync(path, cancellationToken);
        CliResult status = await RunAsync(path, ["login", "status"], cancellationToken);
        // Codex reports its sign-in on stderr.
        string report = status.Stdout + "\n" + status.Stderr;
        bool signedIn = status.ExitCode == 0 && !report.Contains("not logged in", StringComparison.OrdinalIgnoreCase);
        string? account = report.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "ChatGPT plan"
            : report.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "API key" : null;
        // The bundled catalog lists the plan's models; Praxis runs Codex without the owner's own config.
        CliResult catalog = await RunAsync(path, ["debug", "models", "--bundled"], cancellationToken);
        return new ModelTool(ModelTool.Codex, path, version, signedIn, account, signedIn ? null : "signed_out",
            [new ModelChoice("default", "Plan default", ["low", "medium", "high", "xhigh"], null), .. ParseCodexCatalog(catalog.Stdout)]);
    }

    /// <summary>The listed models of a <c>codex debug models</c> catalog with their reasoning efforts.</summary>
    internal static IReadOnlyList<ModelChoice> ParseCodexCatalog(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<ModelChoice> choices = [];
            foreach (JsonElement model in models.EnumerateArray().Take(100))
            {
                if (model.TryGetProperty("visibility", out JsonElement visibility) && visibility.GetString() != "list" ||
                    !model.TryGetProperty("slug", out JsonElement slug) || slug.GetString() is not { Length: > 0 and <= 100 } id)
                {
                    continue;
                }

                string label = model.TryGetProperty("display_name", out JsonElement name) ? name.GetString() ?? id : id;
                string[] efforts = model.TryGetProperty("supported_reasoning_levels", out JsonElement levels) && levels.ValueKind == JsonValueKind.Array
                    ? levels.EnumerateArray().Select(level => level.TryGetProperty("effort", out JsonElement effort) ? effort.GetString() : null)
                        .OfType<string>().Where(effort => Praxis.Domain.Models.ModelEffort.IsValid(effort)).ToArray()
                    : [];
                string? fallback = model.TryGetProperty("default_reasoning_level", out JsonElement standard) ? standard.GetString() : null;
                choices.Add(new ModelChoice(id, label, efforts, fallback));
            }

            return choices;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Ollama's version, and whether its server is running (its models are listed from the server).</summary>
    private static async Task<ModelTool> OllamaAsync(string path, CancellationToken cancellationToken)
    {
        CliResult result = await RunAsync(path, ["--version"], cancellationToken);
        string[] lines = (result.Stdout + "\n" + result.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? version = lines.Select(line => line.Split("version is ", 2)).Where(parts => parts.Length == 2).Select(parts => parts[1]).FirstOrDefault()
                          ?? lines.FirstOrDefault(line => line.StartsWith("ollama version", StringComparison.OrdinalIgnoreCase))?.Split(' ')[^1];
        bool running = !lines.Any(line => line.Contains("could not connect", StringComparison.OrdinalIgnoreCase));
        return new ModelTool(ModelTool.Ollama, path, version, running, null, running ? null : "not_running", []);
    }

    private static async Task<string?> VersionAsync(string path, CancellationToken cancellationToken)
    {
        CliResult result = await RunAsync(path, ["--version"], cancellationToken);
        string version = result.Stdout.Trim().Split('\n')[0];
        return result.ExitCode == 0 && version.Length is > 0 and <= 100 ? version : null;
    }

    private static async Task<CliResult> RunAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        using CliWorkspace workspace = new();
        return await CliProcess.RunAsync(path, arguments, null, workspace.Path, ProbeBudget, cancellationToken);
    }

    private static ModelChoice Claude(string id, string label) => new(id, label, ClaudeEfforts, null);
}
