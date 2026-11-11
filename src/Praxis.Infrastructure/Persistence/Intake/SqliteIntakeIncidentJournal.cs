using Microsoft.Data.Sqlite;
using Praxis.Application.Intake;
using Praxis.Domain.Intake;

namespace Praxis.Infrastructure.Persistence.Intake;

/// <summary>Bounded, transactional watcher-incident history owned by the private catalog.</summary>
public sealed class SqliteIntakeIncidentJournal : IIntakeIncidentJournal
{
    private const int RetainedIncidents = 512;
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteIntakeIncidentJournal(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public void RecordState(string rootPath, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath) || rootPath.Length > 4_096)
        {
            throw new ArgumentException("The watched folder path is invalid.", nameof(rootPath));
        }

        if (state is not ("offline" or "degraded" or "reapproval_required" or "watching"))
        {
            return;
        }

        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand active = connection.CreateCommand("""
                                                                  SELECT incident_id, state FROM intake_incidents
                                                                  WHERE root_path = $root AND resolved_utc IS NULL
                                                                  """);
            active.Transaction = transaction;
            active.Parameters.AddWithValue("$root", rootPath);
            long? incidentId = null;
            string? existing = null;
            using (SqliteDataReader reader = active.ExecuteReader())
            {
                if (reader.Read())
                {
                    incidentId = reader.GetInt64(0);
                    existing = reader.GetString(1);
                }
            }

            if (existing == state)
            {
                return;
            }

            if (incidentId is { } id)
            {
                connection.Run(transaction, """
                                            UPDATE intake_incidents SET resolved_utc = $now
                                            WHERE incident_id = $id
                                            """, ("$now", now), ("$id", id));
            }

            if (state != "watching")
            {
                connection.Run(transaction, """
                                            INSERT INTO intake_incidents (root_path, state, started_utc)
                                            VALUES ($root, $state, $now)
                                            """, ("$root", rootPath), ("$state", state), ("$now", now));

                connection.Run(transaction, """
                                            DELETE FROM intake_incidents WHERE incident_id NOT IN
                                                (SELECT incident_id FROM intake_incidents
                                                 ORDER BY incident_id DESC LIMIT $limit)
                                            """, ("$limit", RetainedIncidents));
            }

            transaction.Commit();
        });
    }

    public IReadOnlyList<IntakeIncident> Recent(int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT incident_id, root_path, state, started_utc, resolved_utc
                                                                 FROM intake_incidents ORDER BY incident_id DESC LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<IntakeIncident> incidents = new();
            while (reader.Read())
            {
                incidents.Add(new IntakeIncident(reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2), CatalogValues.ParseTimestamp(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : CatalogValues.ParseTimestamp(reader.GetString(4))));
            }

            return incidents;
        });
    }
}
