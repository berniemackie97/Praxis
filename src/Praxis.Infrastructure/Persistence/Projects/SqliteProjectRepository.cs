using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Policy;
using Praxis.Application.Projects;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Projects;
using Praxis.Domain.Verification;

namespace Praxis.Infrastructure.Persistence.Projects;

/// <summary>Code project understandings, keyed by project root, and the active file sources they are detected from.</summary>
public sealed class SqliteProjectRepository : IProjectRepository
{
    private readonly CatalogDatabase database;
    private readonly IPolicyRepository policy;
    private readonly TimeProvider clock;

    internal SqliteProjectRepository(CatalogDatabase database, IPolicyRepository policy, TimeProvider clock)
    {
        this.database = database;
        this.policy = policy;
        this.clock = clock;
    }

    public IReadOnlyList<ProjectSource> ReadActiveSources(int limit) =>
        database.ExecuteRead(connection => ActiveSources(connection, limit, null));

    public IReadOnlyList<ProjectSource> ReadCurrentMembers(IReadOnlyList<ProjectSource> expected)
    {
        if (expected.Count == 0)
        {
            return [];
        }

        var byId = expected.ToDictionary(member => member.SourceId);
        string ids = JsonSerializer.Serialize(byId.Keys.Select(id => id.ToString("D")));
        IReadOnlyList<ProjectSource> active = database.ExecuteRead(connection => ActiveSources(connection, expected.Count, ids));
        return active.Where(member => byId.TryGetValue(member.SourceId, out ProjectSource? prior) && member == prior).ToArray();
    }

    private static IReadOnlyList<ProjectSource> ActiveSources(SqliteConnection connection, int limit, string? ids)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT s.source_id, i.item_id, v.revision, s.locator, v.digest
            FROM sources s
            JOIN items i ON i.source_id = s.source_id
            JOIN item_versions v ON v.item_id = i.item_id
            WHERE s.status = 'active' AND s.source_kind = 'file'
              AND ($ids IS NULL OR s.source_id IN (SELECT value FROM json_each($ids)))
              AND v.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = i.item_id)
            ORDER BY s.locator
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$ids", (object?)ids ?? DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        List<ProjectSource> sources = [];
        while (reader.Read())
        {
            sources.Add(new ProjectSource(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4)));
        }

        return sources;
    }

    public ProjectUnderstanding? Read(string projectId) => Query("WHERE project_id = $id", ("$id", projectId)).SingleOrDefault();

    public IReadOnlyList<ProjectUnderstanding> List() => Query("ORDER BY name");

    public void Save(ProjectUnderstanding project) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string details = JsonSerializer.Serialize(new Details(project.Purpose, project.Summary, project.Components, project.EntryPoints, project.BuildCommands,
            project.Dependencies, project.Languages, project.Sources));
        connection.Run(transaction, """
            INSERT INTO intake_projects (project_id, root, name, fingerprint, status, reason, details, members, tier, provider_id, input_tokens, output_tokens, updated_utc)
            VALUES ($id, $root, $name, $fingerprint, $status, $reason, $details, $members, $tier, $provider, $input, $output, $now)
            ON CONFLICT(project_id) DO UPDATE SET root = excluded.root, name = excluded.name, fingerprint = excluded.fingerprint, status = excluded.status,
                reason = excluded.reason, details = excluded.details, members = excluded.members, tier = excluded.tier, provider_id = excluded.provider_id,
                input_tokens = intake_projects.input_tokens + excluded.input_tokens, output_tokens = intake_projects.output_tokens + excluded.output_tokens,
                updated_utc = excluded.updated_utc
            """, ("$id", project.ProjectId), ("$root", project.Root), ("$name", project.Name), ("$fingerprint", project.Fingerprint), ("$status", project.Status),
            ("$reason", (object?)project.Reason ?? DBNull.Value), ("$details", details), ("$members", project.Members),
            ("$tier", (object?)project.Tier ?? DBNull.Value), ("$provider", (object?)project.ProviderId ?? DBNull.Value), ("$input", project.InputTokens),
            ("$output", project.OutputTokens), ("$now", CatalogValues.Timestamp(project.UpdatedUtc)));
        transaction.Commit();
        return true;
    });

    public int RemoveExcept(IReadOnlyCollection<string> projectIds) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        int removed = connection.Run(transaction, "DELETE FROM intake_projects WHERE project_id NOT IN (SELECT value FROM json_each($ids))",
            ("$ids", JsonSerializer.Serialize(projectIds)));
        transaction.Commit();
        return removed;
    });

    private IReadOnlyList<ProjectUnderstanding> Query(string clause, params (string Name, object Value)[] parameters) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT project_id, root, name, fingerprint, status, reason, details, members, tier, provider_id, input_tokens, output_tokens, updated_utc
            FROM intake_projects {clause}
            """);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProjectUnderstanding> projects = [];
        while (reader.Read())
        {
            Details details = JsonSerializer.Deserialize<Details>(reader.GetString(6))!;
            projects.Add(new ProjectUnderstanding(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), details.Purpose, details.Summary, details.Components, details.EntryPoints, details.BuildCommands,
                details.Dependencies, details.Languages, reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11), CatalogValues.ParseTimestamp(reader.GetString(12)),
                details.Sources));
        }

        return (IReadOnlyList<ProjectUnderstanding>)projects;
    }).Select(Current).ToArray();

    private ProjectUnderstanding Current(ProjectUnderstanding project)
    {
        if (project.Status == ProjectUnderstanding.MapOnly)
        {
            return project;
        }

        if (project.Sources is not { Count: > 0 } sources || project.ProviderId is not { } providerId ||
            project.Tier is not { } tier || policy.ReadTiers().FirstOrDefault(candidate => candidate.Tier == tier)?.ProviderId != providerId)
        {
            return WithoutDerivedData(project);
        }

        ProjectSource[] expected = sources.Select(source =>
            new ProjectSource(source.SourceId, source.ItemId, source.Revision, source.Locator, source.Digest)).ToArray();
        if (ReadCurrentMembers(expected).Count != expected.Length)
        {
            return WithoutDerivedData(project);
        }

        DisclosurePolicy settings = policy.ReadPolicy();
        DateTimeOffset now = clock.GetUtcNow();
        foreach (ProjectSummarySource source in sources)
        {
            SourceCategory? category = policy.ReadCategory(source.SourceId);
            string currentCategory = category?.Category ?? DataCategories.Unclassified;
            bool localOnly = category?.LocalOnly ?? false;
            if (currentCategory != source.Category || localOnly != source.LocalOnly ||
                (providerId != VerificationProvider.Ollama &&
                 !DisclosureEvaluator.Decide(settings, currentCategory, providerId, false, DisclosurePurposes.Intake, localOnly, now).Allowed))
            {
                return WithoutDerivedData(project);
            }
        }

        return project;
    }

    private static ProjectUnderstanding WithoutDerivedData(ProjectUnderstanding project) =>
        project with
        {
            Status = ProjectUnderstanding.MapOnly, Reason = "source_scope_changed", Purpose = null, Summary = null,
            Components = [], EntryPoints = [], BuildCommands = [], Dependencies = [],
            Languages = new Dictionary<string, int>(), Tier = null, ProviderId = null, Sources = null
        };

    private sealed record Details(
        string? Purpose, string? Summary, IReadOnlyList<ProjectComponent> Components, IReadOnlyList<string> EntryPoints, IReadOnlyList<string> BuildCommands,
        IReadOnlyList<string> Dependencies, IReadOnlyDictionary<string, int> Languages,
        IReadOnlyList<ProjectSummarySource>? Sources = null);
}
