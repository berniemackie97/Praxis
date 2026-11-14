using System.Globalization;
using Microsoft.Data.Sqlite;
using Praxis.Application.Activity;

namespace Praxis.Infrastructure.Persistence.Activity;

/// <summary>The Activity view's facts in one read: running jobs, per-stage counts, recent model calls, provider usage and intake outcomes.</summary>
public sealed class SqliteActivityReader : IActivityReader
{
    private readonly CatalogDatabase database;

    internal SqliteActivityReader(CatalogDatabase database) => this.database = database;

    public ActivityFacts Read(DateTimeOffset now, int recentCalls)
    {
        string today = CatalogValues.Timestamp(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero));
        string hourAgo = CatalogValues.Timestamp(now.AddHours(-1));
        string weekAgo = CatalogValues.Timestamp(now.AddDays(-7));
        int limit = Math.Clamp(recentCalls, 1, 200);
        return database.ExecuteRead(connection => new ActivityFacts(
            Running(connection), Stages(connection, today), Calls(connection, limit), Providers(connection, today, hourAgo, weekAgo), Outcomes(connection)));
    }

    private static List<RunningJob> Running(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT j.item_id, s.locator, j.processor_key, j.attempt, j.updated_utc, j.lease_until_utc
            FROM processing_jobs j
            JOIN items i ON i.item_id = j.item_id
            JOIN sources s ON s.source_id = i.source_id
            WHERE j.state = 'running'
            ORDER BY j.updated_utc, j.item_id
            LIMIT 50
            """);
        command.CommandTimeout = 5;
        using SqliteDataReader reader = command.ExecuteReader();
        List<RunningJob> jobs = [];
        while (reader.Read())
        {
            jobs.Add(new RunningJob(Guid.Parse(reader.GetString(0)), Path.GetFileName(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3),
                CatalogValues.ParseTimestamp(reader.GetString(4)), reader.IsDBNull(5) ? null : CatalogValues.ParseTimestamp(reader.GetString(5))));
        }

        return jobs;
    }

    private static List<StageCount> Stages(SqliteConnection connection, string today)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT processor_key,
                   SUM(state = 'queued'), SUM(state = 'running'), SUM(state = 'failed'),
                   SUM(state IN ('completed', 'unsupported') AND updated_utc >= $today)
            FROM processing_jobs
            GROUP BY processor_key
            ORDER BY processor_key
            """);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("$today", today);
        using SqliteDataReader reader = command.ExecuteReader();
        List<StageCount> stages = [];
        while (reader.Read())
        {
            stages.Add(new StageCount(reader.GetString(0), Int(reader, 1), Int(reader, 2), Int(reader, 3), Int(reader, 4)));
        }

        return stages;
    }

    private static List<RecentModelCall> Calls(SqliteConnection connection, int limit)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT m.started_utc, m.tier, m.provider_id, m.model_name, m.purpose, m.outcome, s.locator, m.duration_ms, m.input_tokens, m.output_tokens
            FROM model_invocations m
            LEFT JOIN items i ON i.item_id = m.item_id
            LEFT JOIN sources s ON s.source_id = i.source_id
            ORDER BY m.invocation_id DESC
            LIMIT $limit
            """);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<RecentModelCall> calls = [];
        while (reader.Read())
        {
            calls.Add(new RecentModelCall(CatalogValues.ParseTimestamp(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : Path.GetFileName(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9)));
        }

        return calls;
    }

    private static List<ProviderUsage> Providers(SqliteConnection connection, string today, string hourAgo, string weekAgo)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT m.provider_id,
                   SUM(m.started_utc >= $today),
                   SUM(m.started_utc >= $hour AND m.outcome <> 'completed'),
                   MAX(m.started_utc),
                   (SELECT latest.outcome FROM model_invocations latest
                    WHERE latest.provider_id = m.provider_id
                    ORDER BY latest.started_utc DESC, latest.invocation_id DESC LIMIT 1)
            FROM model_invocations m
            WHERE m.started_utc >= $week
            GROUP BY m.provider_id
            ORDER BY m.provider_id
            """);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("$today", today);
        command.Parameters.AddWithValue("$hour", hourAgo);
        command.Parameters.AddWithValue("$week", weekAgo);
        using SqliteDataReader reader = command.ExecuteReader();
        List<ProviderUsage> providers = [];
        while (reader.Read())
        {
            providers.Add(new ProviderUsage(reader.GetString(0), Int(reader, 1), Int(reader, 2),
                reader.IsDBNull(3) ? null : CatalogValues.ParseTimestamp(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return providers;
    }

    private static List<IntakeOutcomeCount> Outcomes(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT d.status, d.reason, COUNT(*)
            FROM intake_documents d
            JOIN items i ON i.item_id = d.item_id
            JOIN sources s ON s.source_id = i.source_id AND s.status = 'active'
            WHERE d.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = d.item_id)
            GROUP BY d.status, d.reason
            ORDER BY COUNT(*) DESC, d.status, d.reason
            """);
        command.CommandTimeout = 5;
        using SqliteDataReader reader = command.ExecuteReader();
        List<IntakeOutcomeCount> outcomes = [];
        while (reader.Read())
        {
            outcomes.Add(new IntakeOutcomeCount(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), Int(reader, 2)));
        }

        return outcomes;
    }

    private static int Int(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
