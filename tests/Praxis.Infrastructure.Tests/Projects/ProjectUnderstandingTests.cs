using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Policy;
using Praxis.Application.Projects;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Policy;
using Praxis.Domain.Projects;
using Praxis.Domain.Understanding;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Agents;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Projects;

/// <summary>Code projects are understood as a whole from a repository map, verified in code; code files cost no per-file model call.</summary>
public sealed class ProjectUnderstandingTests : IDisposable
{
    private const string Local = VerificationProvider.Ollama;
    private const string Strong = VerificationProvider.ClaudeCode;
    private const string Strongest = VerificationProvider.Codex;

    private const string PackageJson = """
        {"name":"invoice-api","scripts":{"dev":"tsx src/server.ts","test":"vitest"},
         "dependencies":{"express":"^4.19.0","pg":"^8.11.0"},"devDependencies":{"vitest":"^1.6.0"}}
        """;

    private const string Readme = """
        # Invoice API
        A small service that stores invoices in Postgres and serves them over HTTP.
        Run `npm run dev` for development and `docker compose up` for the database.
        """;

    private const string Server = """
        import express from "express";
        export async function startServer(port: number) { }
        export class InvoiceRoutes { }
        """;

    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-projects-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    public ProjectUnderstandingTests()
    {
        Directory.CreateDirectory(Path.Combine(workspace, "store"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(workspace, "store"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData(".cs", "public sealed class Ledger\n{\n    public async Task<int> PostAsync() { }\n}\ninternal record Entry(int Id);", "public sealed class Ledger")]
    [InlineData(".ts", "export async function startServer(port: number) {\n}\nexport class Routes {}", "export async function startServer(port: number)")]
    [InlineData(".py", "class Importer:\n    def run(self):\n        pass\ndef main():\n    pass", "class Importer")]
    [InlineData(".swift", "struct ContentView: View {\n    func load() {}\n}", "struct ContentView: View")]
    [InlineData(".go", "func (s *Server) Start() error {\n}\ntype Server struct {}", "func (s *Server) Start() error")]
    public void OutlinesNameWhatAFileDefines(string extension, string code, string expected) =>
        Assert.Contains(expected, CodeOutline.Extract(code, extension));

    [Fact]
    public void ManifestsDeclareDependenciesAndCommands()
    {
        Assert.Equal(["express", "pg", "vitest"], ProjectManifests.Dependencies("package.json", PackageJson).Order());
        Assert.Contains("npm run dev", ProjectManifests.Commands("package.json", PackageJson));
        Assert.Equal(["Serilog"], ProjectManifests.Dependencies("App.csproj", """<ItemGroup><PackageReference Include="Serilog" Version="3.1.1" /></ItemGroup>"""));
        Assert.Equal(["serde", "tokio"], ProjectManifests.Dependencies("Cargo.toml", "[package]\nname=\"x\"\n[dependencies]\nserde = \"1\"\ntokio = { version = \"1\" }\n").Order());
        Assert.Equal(["github.com/go-chi/chi/v5"], ProjectManifests.Dependencies("go.mod", "module x\nrequire (\n\tgithub.com/go-chi/chi/v5 v5.0.12\n)\n"));
        Assert.Contains("dotnet test", ProjectManifests.Commands("App.csproj", string.Empty));
    }

    [Fact]
    public void NestedMarkersBelongToTheOutermostProject()
    {
        string root = Path.Combine(workspace, "repo");
        ProjectSource[] sources =
        [
            Source(Path.Combine(root, "Repo.sln")), Source(Path.Combine(root, "src", "App", "App.csproj")), Source(Path.Combine(root, "src", "App", "Program.cs")),
            Source(Path.Combine(workspace, "notes", "todo.md"))
        ];

        ProjectCandidate project = Assert.Single(ProjectDetector.Detect(sources));

        Assert.Equal(root, project.Root);
        Assert.Equal(3, project.Members.Count);
        Assert.Equal("src/App/Program.cs", project.Relative(project.Members.Single(member => member.Locator.EndsWith(".cs", StringComparison.Ordinal))));
        string before = ProjectDetector.Fingerprint(project);
        Assert.NotEqual(before, ProjectDetector.Fingerprint(project with { Members = [.. project.Members.SkipLast(1)] }));
    }

    [Fact]
    public void GitRepositoryMarkersGroupCapturedNotesWithoutImportingGitMetadata()
    {
        string root = Path.Combine(workspace, "notes-repository");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        ProjectSource note = Source(Path.Combine(root, "evidence", "investigation.md"));
        ProjectSource unrelated = Source(Path.Combine(workspace, "elsewhere", "todo.md"));

        IReadOnlyCollection<string> roots = GitProjectRoots.Find([note, unrelated], [workspace]);
        Assert.Equal(root, Assert.Single(roots));
        ProjectCandidate project = Assert.Single(ProjectDetector.Detect([note, unrelated], roots));
        Assert.Equal(root, project.Root);
        Assert.Equal(note.Locator, Assert.Single(project.Members).Locator);
    }

    [Fact]
    public async Task GitOnlyNotesProjectRemainsLocalWithoutABuildMarker()
    {
        using SqliteCatalog catalog = Open();
        await Capture(catalog, "notes-repository/investigation.md", "# Research notes");
        ScriptedModelFactory models = new();
        string root = Path.Combine(workspace, "incoming", "notes-repository");
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock),
            catalog.Policy, clock, _ => [root]);

        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding project = Assert.Single(catalog.Projects.List());
        Assert.Equal((ProjectUnderstanding.MapOnly, "local_repository_map"), (project.Status, project.Reason));
        Assert.Empty(catalog.Policy.ReadInvocations(10));
    }

    [Fact]
    public async Task AProjectIsUnderstoodOnceFromItsMapAndUnverifiedClaimsAreDropped()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Code, Strong);
        Grant(catalog, DataCategories.Documents, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory()
            .Answer(Local, _ => """{"category":"documents","documentType":"readme","language":"en","gist":"Project readme.","recordKinds":[]}""")
            .Answer(Strong, request => request.Messages[^1].Content.Contains("Declared dependencies", StringComparison.Ordinal)
                ? """
                  {"purpose":"An HTTP service that stores invoices in Postgres.","summary":"Express routes over a pg pool.",
                   "components":[{"name":"Server","path":"src/server.ts","role":"HTTP entry"},{"name":"Billing","path":"src/billing/","role":"invented"},
                                 {"name":"Sources","path":"src/","role":"application code"},{"name":"Docs","path":"README.md","role":"overview"}],
                   "entryPoints":["src/server.ts"],
                   "buildCommands":["npm run dev","docker compose up"]}
                  """
                : """{"summary":"Readme.","documentType":"readme","topics":[],"entities":[],"relations":[],"records":[]}""");
        await Capture(catalog, "invoice-api/package.json", PackageJson);
        await Capture(catalog, "invoice-api/README.md", Readme);
        CapturedOriginal server = await Capture(catalog, "invoice-api/src/server.ts", Server);
        await Capture(catalog, "notes/groceries.md", "milk");
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        SourceUnderstanding code = catalog.Understanding.ReadCurrent(server.SourceId)!;
        Assert.Equal((UnderstandingStatuses.IndexOnly, "project_level", 0L), (code.Status, code.Reason, code.InputTokens));
        int strongBefore = models.Count(Strong);

        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));

        Assert.Equal(strongBefore + 1, models.Count(Strong));
        Assert.Equal(0, models.Count(Strongest));
        ProjectUnderstanding project = Assert.Single(catalog.Projects.List());
        Assert.Equal(("invoice-api", ProjectUnderstanding.Understood, ModelTiers.Strong), (project.Name, project.Status, project.Tier));
        Assert.Equal(["src/server.ts", "src/", "README.md"], project.Components.Select(component => component.Path));
        Assert.Equal(["npm run dev", "docker compose up"], project.BuildCommands);
        Assert.Equal(["express", "pg", "vitest"], project.Dependencies);
        Assert.Equal(3, project.Members);

        // Unchanged files: no new call. Changed files inside the interval: still none.
        Assert.Equal(0, await understander.RunAsync(CancellationToken.None));
        await Capture(catalog, "invoice-api/src/db.ts", "export const pool = 1;");
        Assert.Equal(0, await understander.RunAsync(CancellationToken.None));
        clock.UtcNow += ProjectUnderstander.MinimumInterval;
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));

        AgentToolOutput listed = await new KnowledgeProjectTool(catalog.Projects).InvokeAsync(JsonDocument.Parse("{}").RootElement, AgentPrimitivesTests.Context(),
            CancellationToken.None);
        Assert.Contains("An HTTP service that stores invoices in Postgres.", listed.Content, StringComparison.Ordinal);
        AgentToolOutput opened = await new KnowledgeProjectTool(catalog.Projects).InvokeAsync(
            JsonDocument.Parse("""{"name":"invoice-api"}""").RootElement, AgentPrimitivesTests.Context(), CancellationToken.None);
        Assert.Contains("npm run dev", opened.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectMapsApplyMemberDisclosureSeparatelyToStrongAndStrongest()
    {
        using SqliteCatalog catalog = Open();
        ScriptedModelFactory models = new ScriptedModelFactory()
            .Answer(Local, _ => """{"category":"documents","documentType":"notes","language":"en","gist":"notes","recordKinds":[]}""")
            .Answer(Strong, _ => """
                {"purpose":"Project.","summary":"Review required.",
                 "components":[{"name":"Invalid","path":"missing.ts","role":"missing"}],"entryPoints":[],"buildCommands":[]}
                """)
            .Answer(Strongest, _ => """
                {"purpose":"Project.","summary":"Verified.",
                 "components":[{"name":"Public","path":"src/public.ts","role":"entry"}],"entryPoints":[],"buildCommands":[]}
                """);
        CapturedOriginal marker = await Capture(catalog, "scoped/package.json", PackageJson);
        CapturedOriginal allowed = await Capture(catalog, "scoped/src/public.ts", Server);
        CapturedOriginal readme = await Capture(catalog, "scoped/README.md", Readme);
        CapturedOriginal privateFile = await Capture(catalog, "scoped/src/localOnly.ts", "export class SecretLocal {}");
        CapturedOriginal unknown = await Capture(catalog, "scoped/src/unknown.ts", "export class SecretUnknown {}");
        CapturedOriginal revoked = await Capture(catalog, "scoped/src/revoked.ts", "export class SecretRevoked {}");
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(allowed.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(readme.SourceId, DataCategories.Documents, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(privateFile.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, true));
        catalog.Policy.SaveCategory(new SourceCategory(unknown.SourceId, DataCategories.Unclassified, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(revoked.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        Grant(catalog, DataCategories.Code, Strongest);
        Grant(catalog, DataCategories.Documents, Strongest);

        ProjectCandidate captured = Assert.Single(ProjectDetector.Detect(catalog.Projects.ReadActiveSources(100)));
        Assert.Contains(captured.Members, member => member.SourceId == revoked.SourceId);
        Assert.True(catalog.Sources.Revoke(revoked.SourceId));

        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        ProjectUnderstanding project = await understander.UnderstandAsync(captured, ProjectDetector.Fingerprint(captured), CancellationToken.None);
        Assert.Equal((ProjectUnderstanding.Understood, ModelTiers.Strongest), (project.Status, project.Tier));

        string strong = Assert.Single(models.Requests[Strong], call => call.Messages[^1].Content.StartsWith("Project folder:", StringComparison.Ordinal))
            .Messages[^1].Content;
        string strongest = Assert.Single(models.Requests[Strongest], call => call.Messages[^1].Content.StartsWith("Project folder:", StringComparison.Ordinal))
            .Messages[^1].Content;
        Assert.Contains("src/public.ts", strong, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", strong, StringComparison.Ordinal);
        Assert.Contains("README.md", strongest, StringComparison.Ordinal);
        foreach (string withheld in new[] { "localOnly.ts", "unknown.ts", "revoked.ts", "SecretLocal", "SecretUnknown", "SecretRevoked" })
        {
            Assert.DoesNotContain(withheld, strong, StringComparison.Ordinal);
            Assert.DoesNotContain(withheld, strongest, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProjectScopeRejectsDeniedOrChangedSourcesBeforeModelDispatch()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "protected/package.json", PackageJson);
        CapturedOriginal member = await Capture(catalog, "protected/src/private.ts", Server);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, true));
        Grant(catalog, DataCategories.Code, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => "{}");
        RepositoryMap map = RepositoryMap.Build(Assert.Single(ProjectDetector.Detect(catalog.Projects.ReadActiveSources(100))),
            catalog.Projects.ReadActiveSources(100), _ => null);
        TieredRequest request = new(marker.SourceId, null, null, ProjectUnderstander.ProcessorKey, DisclosurePurposes.Intake,
            ProjectPrompt.Create(map), DisclosureSources: [marker.SourceId, member.SourceId], SourceScopeCurrent: () => true);
        TieredModels tier = CatalogProcessing.Tiers(catalog, models, clock);

        TieredOutcome denied = await tier.CompleteAsync(ModelTiers.Strong, request, CancellationToken.None);
        Assert.Equal((TieredOutcome.Disallowed, DisclosureDecision.LocalOnlySpace), (denied.Status, denied.Decision!.Reason));
        Assert.Equal(0, models.Count(Strong));

        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        TieredOutcome stale = await tier.CompleteAsync(ModelTiers.Strong,
            request with { SourceScopeCurrent = () => false }, CancellationToken.None);
        Assert.Equal((TieredOutcome.Disallowed, DisclosureDecision.SourceChanged), (stale.Status, stale.Decision!.Reason));
        Assert.Equal(0, models.Count(Strong));

        TieredOutcome reassigned = await tier.CompleteAsync(ModelTiers.Strong,
            request with
            {
                SourceScopeCurrent = () =>
            {
                catalog.Policy.SaveTier(ModelTiers.Strong, Strongest);
                return true;
            }
            }, CancellationToken.None);
        Assert.Equal((TieredOutcome.NotConfigured, "tier_changed"), (reassigned.Status, reassigned.FailureCode));
        Assert.Equal(0, models.Count(Strong));
    }

    [Fact]
    public async Task ReplacedProjectSourceCannotRejoinAnOlderSnapshot()
    {
        using SqliteCatalog catalog = Open();
        await Capture(catalog, "current/package.json", PackageJson);
        ProjectSource original = Assert.Single(catalog.Projects.ReadActiveSources(100));
        Assert.Equal(original, Assert.Single(catalog.Projects.ReadCurrentMembers([original])));

        await Capture(catalog, "current/package.json", """{"name":"changed"}""", "current/package.json-revision-2");
        Assert.Empty(catalog.Projects.ReadCurrentMembers([original]));
        ProjectSource latest = Assert.Single(catalog.Projects.ReadActiveSources(100));
        Assert.NotEqual(original.Revision, latest.Revision);
        Assert.Equal(latest, Assert.Single(catalog.Projects.ReadCurrentMembers([latest])));
    }

    [Fact]
    public async Task ProjectSummaryIsNotPublishedAfterAMemberIsRevokedDuringInference()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "volatile/package.json", PackageJson);
        CapturedOriginal member = await Capture(catalog, "volatile/src/public.ts", Server);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ =>
        {
            Assert.True(catalog.Sources.Revoke(member.SourceId));
            return """
                {"purpose":"A service.","summary":"Uses the source file.",
                 "components":[{"name":"Entry","path":"src/public.ts","role":"entry"}],"entryPoints":[],"buildCommands":[]}
                """;
        });
        ProjectCandidate project = Assert.Single(ProjectDetector.Detect(catalog.Projects.ReadActiveSources(100)));
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);

        ProjectUnderstanding result = await understander.UnderstandAsync(project, ProjectDetector.Fingerprint(project), CancellationToken.None);
        Assert.Equal((ProjectUnderstanding.MapOnly, "source_scope_changed"), (result.Status, result.Reason));
        Assert.Null(result.Summary);
        Assert.Empty(result.Components);
        Assert.Equal(1, models.Count(Strong));
    }

    [Fact]
    public async Task RemoteProviderCannotActAsLocalProjectFallback()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "remote-local/package.json", PackageJson);
        CapturedOriginal member = await Capture(catalog, "remote-local/src/private.ts", "export class PrivateRoutine {}");
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Personal, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        Grant(catalog, DataCategories.Personal, Strong);
        catalog.Policy.SaveTier(ModelTiers.Strong, null);
        catalog.Policy.SaveTier(ModelTiers.Strongest, null);
        catalog.Policy.SaveTier(ModelTiers.Local, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => "{}");
        ProjectCandidate project = Assert.Single(ProjectDetector.Detect(catalog.Projects.ReadActiveSources(100)));
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);

        ProjectUnderstanding result = await understander.UnderstandAsync(project, ProjectDetector.Fingerprint(project), CancellationToken.None);
        Assert.Equal(ProjectUnderstanding.MapOnly, result.Status);
        Assert.Equal(0, models.Count(Strong));
    }

    [Fact]
    public async Task CategoryChangesDuringInferenceInvalidateProjectSummary()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "category-change/package.json", PackageJson);
        CapturedOriginal member = await Capture(catalog, "category-change/src/public.ts", Server);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        Grant(catalog, DataCategories.Personal, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ =>
        {
            catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Personal, SourceCategory.OwnerOrigin, false));
            return """
                {"purpose":"A service.","summary":"Contains source functions.",
                 "components":[{"name":"Public","path":"src/public.ts","role":"entry"}],"entryPoints":[],"buildCommands":[]}
                """;
        });
        ProjectCandidate project = Assert.Single(ProjectDetector.Detect(catalog.Projects.ReadActiveSources(100)));
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);

        ProjectUnderstanding result = await understander.UnderstandAsync(project, ProjectDetector.Fingerprint(project), CancellationToken.None);
        Assert.Equal((ProjectUnderstanding.MapOnly, "source_scope_changed"), (result.Status, result.Reason));
        Assert.Null(result.Summary);
        Assert.Empty(result.Components);
        Assert.Equal(1, models.Count(Strong));
    }

    [Fact]
    public async Task ExpiredProjectDisclosureGrantDoesNotAuthorizeRemoteCall()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "expiring/package.json", PackageJson);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        new PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Code,
            Strong, DisclosurePurposes.Intake, true, clock.UtcNow.AddMinutes(1))), PolicyActors.Owner);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => "{}");
        RepositoryMap map = RepositoryMap.Build(Assert.Single(ProjectDetector.Detect(catalog.Projects.ReadActiveSources(100))),
            catalog.Projects.ReadActiveSources(100), _ => null);
        TieredRequest request = new(marker.SourceId, null, null, ProjectUnderstander.ProcessorKey, DisclosurePurposes.Intake,
            ProjectPrompt.Create(map), DisclosureSources: [marker.SourceId], SourceScopeCurrent: () => true);

        TieredOutcome outcome = await CatalogProcessing.Tiers(catalog, models, clock)
            .CompleteAsync(ModelTiers.Strong, request, CancellationToken.None);
        Assert.Equal(TieredOutcome.Disallowed, outcome.Status);
        Assert.Equal(0, models.Count(Strong));
    }

    [Fact]
    public async Task CachedProjectRequiresCurrentSourcesAndRecipientDisclosure()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "guarded/package.json", PackageJson);
        CapturedOriginal member = await Capture(catalog, "guarded/src/server.ts", Server);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => """
            {"purpose":"A source-backed service.","summary":"Contains application functions.",
             "components":[{"name":"Server","path":"src/server.ts","role":"application entry"}],
             "entryPoints":["src/server.ts"],"buildCommands":[]}
            """);
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding project = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.Understood, project.Status);
        Assert.Equal(2, project.Sources!.Count);

        KnowledgeProjectTool tool = new(catalog.Projects);
        AgentToolContext remote = AgentPrimitivesTests.Context() with
        {
            BackendId = Strong,
            BackendIsLocal = false,
            MayDisclose = id => id != member.SourceId
        };
        AgentToolOutput deniedList = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, remote, CancellationToken.None);
        Assert.DoesNotContain("guarded", deniedList.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("source-backed", deniedList.Content, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(deniedList.Withheld).Count);

        AgentToolOutput deniedDetail = await tool.InvokeAsync(JsonDocument.Parse("""{"name":"guarded"}""").RootElement,
            remote, CancellationToken.None);
        Assert.Contains("project_not_available", deniedDetail.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("application entry", deniedDetail.Content, StringComparison.Ordinal);

        AgentToolOutput permitted = await tool.InvokeAsync(JsonDocument.Parse("""{"name":"guarded"}""").RootElement,
            remote with { MayDisclose = _ => true }, CancellationToken.None);
        Assert.Contains("A source-backed service.", permitted.Content, StringComparison.Ordinal);

        Assert.True(catalog.Sources.Revoke(member.SourceId));
        ProjectUnderstanding invalid = Assert.Single(catalog.Projects.List());
        Assert.Equal((ProjectUnderstanding.MapOnly, "source_scope_changed"), (invalid.Status, invalid.Reason));
        Assert.Null(invalid.Summary);
        Assert.Empty(invalid.Components);
        Assert.Empty(invalid.Dependencies);
        AgentToolOutput afterRevocation = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement,
            remote with { MayDisclose = _ => true }, CancellationToken.None);
        Assert.DoesNotContain("source-backed", afterRevocation.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("guarded", afterRevocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CategoryChangeInvalidatesCachedProjectWithoutFileChange()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "recategorized/package.json", PackageJson);
        CapturedOriginal member = await Capture(catalog, "recategorized/src/server.ts", Server);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => """
            {"purpose":"A build project.","summary":"Verified.",
             "components":[{"name":"Manifest","path":"package.json","role":"build"}],
             "entryPoints":[],"buildCommands":[]}
            """);
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding before = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.Understood, before.Status);
        Assert.Equal(0, await understander.RunAsync(CancellationToken.None));

        catalog.Policy.SaveCategory(new SourceCategory(member.SourceId, DataCategories.Personal, SourceCategory.OwnerOrigin, false));
        Assert.Equal("source_scope_changed", Assert.Single(catalog.Projects.List()).Reason);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding after = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.Understood, after.Status);
        Assert.Single(after.Sources!);
        Assert.Equal(marker.SourceId, after.Sources![0].SourceId);
        Assert.Equal(2, models.Count(Strong));
    }

    [Fact]
    public async Task LegacyAndExpiredScopeSummariesCannotBeReadAsCurrent()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "stale/package.json", PackageJson);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => """
            {"purpose":"A stale service.","summary":"Sensitive metadata.",
             "components":[{"name":"Manifest","path":"package.json","role":"build"}],
             "entryPoints":[],"buildCommands":[]}
            """);
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding saved = Assert.Single(catalog.Projects.List());
        Assert.NotNull(saved.Sources);

        catalog.Projects.Save(saved with { Sources = null });
        Assert.Equal("source_scope_changed", Assert.Single(catalog.Projects.List()).Reason);

        catalog.Projects.Save(saved);
        new PolicyService(catalog.Policy).Request(new PolicyChange(RemoveRuleId:
            catalog.Policy.ReadPolicy().Rules.Single(rule => rule.ProviderId == Strong && rule.Category == DataCategories.Code).RuleId), PolicyActors.Owner);
        ProjectUnderstanding expired = Assert.Single(catalog.Projects.List());
        Assert.Equal((ProjectUnderstanding.MapOnly, "source_scope_changed"), (expired.Status, expired.Reason));
        Assert.DoesNotContain("Sensitive metadata.", (await new KnowledgeProjectTool(catalog.Projects)
            .InvokeAsync(JsonDocument.Parse("{}").RootElement, AgentPrimitivesTests.Context(), CancellationToken.None)).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupersededSourceAndProviderChangeInvalidatePersistedProject()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "revised/package.json", PackageJson);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        Grant(catalog, DataCategories.Code, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => """
            {"purpose":"A revisioned project.","summary":"Explains package.json.",
             "components":[{"name":"Manifest","path":"package.json","role":"build"}],
             "entryPoints":[],"buildCommands":[]}
            """);
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding saved = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.Understood, saved.Status);

        catalog.Policy.SaveTier(ModelTiers.Strong, Strongest);
        Assert.Equal("source_scope_changed", Assert.Single(catalog.Projects.List()).Reason);

        catalog.Policy.SaveTier(ModelTiers.Strong, Strong);
        catalog.Projects.Save(saved);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, true));
        Assert.Equal("source_scope_changed", Assert.Single(catalog.Projects.List()).Reason);

        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        catalog.Projects.Save(saved);
        await Capture(catalog, "revised/package.json", """{"name":"changed"}""", "revision-2");
        Assert.Equal("source_scope_changed", Assert.Single(catalog.Projects.List()).Reason);
    }

    [Fact]
    public async Task RestoringProjectDisclosureRebuildsAnUnchangedMapOnlyProject()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal marker = await Capture(catalog, "regrant/package.json", PackageJson);
        catalog.Policy.SaveCategory(new SourceCategory(marker.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        DisclosureRule grant = new(Guid.NewGuid(), DataCategories.Code, Strong, DisclosurePurposes.Intake, true, null);
        PolicyService service = new(catalog.Policy);
        service.Request(new PolicyChange(AddRule: grant), PolicyActors.Owner);
        ScriptedModelFactory models = new ScriptedModelFactory().Answer(Strong, _ => """
            {"purpose":"A restorable project.","summary":"Current source metadata.",
             "components":[{"name":"Manifest","path":"package.json","role":"project"}],
             "entryPoints":[],"buildCommands":[]}
            """);
        ProjectUnderstander understander = new(catalog.Projects, catalog.Text, CatalogProcessing.Ladder(catalog, models, clock), catalog.Policy, clock);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding original = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.Understood, original.Status);
        Assert.NotNull(original.DisclosureFingerprint);

        service.Request(new PolicyChange(RemoveRuleId: grant.RuleId), PolicyActors.Owner);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding denied = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.MapOnly, denied.Status);
        Assert.Equal(1, models.Count(Strong));
        Assert.Equal(0, await understander.RunAsync(CancellationToken.None));

        service.Request(new PolicyChange(AddRule: grant with { RuleId = Guid.NewGuid() }), PolicyActors.Owner);
        Assert.Equal(1, await understander.RunAsync(CancellationToken.None));
        ProjectUnderstanding restored = Assert.Single(catalog.Projects.List());
        Assert.Equal(ProjectUnderstanding.Understood, restored.Status);
        Assert.Equal(original.Fingerprint, restored.Fingerprint);
        Assert.Equal(2, models.Count(Strong));
    }

    [Fact]
    public async Task ChatDisclosureReflectsRevocationInsideTheSameTurn()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Capture(catalog, "chat/package.json", PackageJson);
        catalog.Policy.SaveCategory(new SourceCategory(source.SourceId, DataCategories.Code, SourceCategory.OwnerOrigin, false));
        DisclosureRule grant = new(Guid.NewGuid(), DataCategories.Code, Strong, DisclosurePurposes.Chat, true, null);
        new PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: grant), PolicyActors.Owner);
        DisclosureScope scope = new(new DisclosureGate(catalog.Policy, clock), Strong, false);

        Assert.True(scope.MayDisclose(source.SourceId));
        new PolicyService(catalog.Policy).Request(new PolicyChange(RemoveRuleId: grant.RuleId), PolicyActors.Owner);
        Assert.False(scope.MayDisclose(source.SourceId));
        Assert.True(new DisclosureScope(new DisclosureGate(catalog.Policy, clock), Local, true).MayDisclose(source.SourceId));
    }

    [Fact]
    public void VerifierDropsPathsAndCommandsTheProjectDoesNotHave()
    {
        ProjectCandidate project = new("p", "/r", "r", [Source("/r/package.json"), Source("/r/src/index.ts")]);
        RepositoryMap map = RepositoryMap.Build(project, project.Members, member => member.Locator.EndsWith(".json", StringComparison.Ordinal) ? PackageJson : Server);

        ProjectDraft draft = ProjectVerifier.Verify("""
            {"purpose":"x","summary":"y","components":[{"name":"a","path":"lib/","role":"r"}],"entryPoints":["src/index.ts","main.py"],
             "buildCommands":["npm test","rm -rf /"]}
            """, map)!;

        Assert.Equal(["src/index.ts"], draft.EntryPoints);
        Assert.Equal(["npm test"], draft.BuildCommands);
        Assert.Empty(draft.Components);
        Assert.False(draft.Acceptable);
        Assert.Equal(3, draft.Problems.Count);
    }

    private static ProjectSource Source(string path) => new(Guid.NewGuid(), Guid.NewGuid(), 1, path, new string('a', 64));

    private SqliteCatalog Open()
    {
        SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(workspace, "store"), clock);
        catalog.Verification.SaveProvider(new VerificationProvider(Local, null, "qwen:9b", 0, 500, true, "http://127.0.0.1:11434"));
        catalog.Verification.SaveProvider(new VerificationProvider(Strong, "/usr/bin/true", "sonnet", 10, 500, true));
        catalog.Verification.SaveProvider(new VerificationProvider(Strongest, "/usr/bin/true", "gpt", 20, 500, true));
        catalog.Policy.SaveTier(ModelTiers.Local, Local);
        catalog.Policy.SaveTier(ModelTiers.Strong, Strong);
        catalog.Policy.SaveTier(ModelTiers.Strongest, Strongest);
        return catalog;
    }

    private static void Grant(SqliteCatalog catalog, string category, string provider) =>
        new PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), category, provider,
            DisclosurePurposes.Intake, true, null)), PolicyActors.Owner);

    private async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string name, string text, string? requestKey = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using MemoryStream input = new(bytes);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(workspace, "incoming", name), input, bytes.Length, requestKey ?? name);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
