using Microsoft.Data.Sqlite;
using Praxis.Application.Agents.Capabilities;

namespace Praxis.Infrastructure.Persistence.Agents;

public sealed class SqliteCapabilityJournal : ICapabilityJournal
{
    private const string Columns = "id, conversation_id, operation, target, description, status, detail, created_utc";
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteCapabilityJournal(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public CapabilityRequest Propose(Guid conversationId, string operation, string? target, string description) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        DateTimeOffset now = clock.GetUtcNow();
        Expire(connection, transaction, now);
        using (SqliteCommand existing = connection.CreateCommand($"""
            SELECT {Columns} FROM agent_capability_requests
            WHERE conversation_id = $conversation AND operation = $operation AND target IS $target AND status = 'pending' LIMIT 1
            """))
        {
            existing.Transaction = transaction;
            existing.Parameters.AddWithValue("$conversation", conversationId.ToString("D"));
            existing.Parameters.AddWithValue("$operation", operation);
            existing.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
            using SqliteDataReader reader = existing.ExecuteReader();
            if (reader.Read())
            {
                return Map(reader);
            }
        }

        using (SqliteCommand count = connection.CreateCommand("SELECT COUNT(*) FROM agent_capability_requests WHERE status = 'pending'"))
        {
            count.Transaction = transaction;
            if (Convert.ToInt32(count.ExecuteScalar()) >= 24)
            {
                throw new InvalidOperationException("Review pending actions before adding more.");
            }
        }

        Guid id = Guid.NewGuid();
        string timestamp = CatalogValues.Timestamp(now);
        connection.Run(transaction, """
            INSERT INTO agent_capability_requests (id, conversation_id, operation, target, description, status, created_utc, updated_utc)
            VALUES ($id, $conversation, $operation, $target, $description, 'pending', $now, $now)
            """, ("$id", id.ToString("D")), ("$conversation", conversationId.ToString("D")), ("$operation", operation), ("$target", target),
            ("$description", description), ("$now", timestamp));
        transaction.Commit();
        return new CapabilityRequest(id, conversationId, operation, target, description, "pending", null, now);
    });

    public IReadOnlyList<CapabilityRequest> List(int limit) => database.ExecuteRead<IReadOnlyList<CapabilityRequest>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT {Columns} FROM agent_capability_requests
            ORDER BY (status = 'pending' AND created_utc > $cutoff) DESC, created_utc DESC, rowid DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        command.Parameters.AddWithValue("$cutoff", CatalogValues.Timestamp(clock.GetUtcNow().AddMinutes(-30)));
        using SqliteDataReader reader = command.ExecuteReader();
        List<CapabilityRequest> result = [];
        while (reader.Read())
        {
            result.Add(EffectiveStatus(Map(reader)));
        }

        return result;
    });

    public CapabilityRequest? Read(Guid id) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand($"SELECT {Columns} FROM agent_capability_requests WHERE id = $id");
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? EffectiveStatus(Map(reader)) : null;
    });

    public CapabilityRequest? Claim(Guid id, bool accept) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        DateTimeOffset now = clock.GetUtcNow();
        Expire(connection, transaction, now);
        string status = accept ? "running" : "rejected";
        int changed = connection.Run(transaction, """
            UPDATE agent_capability_requests SET status = $status, updated_utc = $now WHERE id = $id AND status = 'pending'
            """, ("$id", id.ToString("D")), ("$status", status), ("$now", CatalogValues.Timestamp(now)));
        transaction.Commit();
        return changed == 1 ? Read(id) : null;
    });

    public void Finish(Guid id, CapabilityExecution result) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string detail = result.Detail[..Math.Min(6_000, result.Detail.Length)];
        connection.Run(transaction, """
            UPDATE agent_capability_requests SET status = $status, detail = $detail, updated_utc = $now
            WHERE id = $id AND status = 'running'
            """, ("$id", id.ToString("D")), ("$status", result.Succeeded ? "accepted" : "failed"), ("$detail", detail),
            ("$now", CatalogValues.Timestamp(clock.GetUtcNow())));
        transaction.Commit();
    });

    private static void Expire(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now) =>
        connection.Run(transaction, """
            UPDATE agent_capability_requests SET status = 'expired', updated_utc = $now
            WHERE status = 'pending' AND created_utc <= $cutoff
            """, ("$now", CatalogValues.Timestamp(now)), ("$cutoff", CatalogValues.Timestamp(now.AddMinutes(-30))));

    private CapabilityRequest EffectiveStatus(CapabilityRequest request) =>
        request.Status == "pending" && request.CreatedUtc <= clock.GetUtcNow().AddMinutes(-30) ? request with { Status = "expired" } : request;

    private static CapabilityRequest Map(SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), Text(reader, 3),
            reader.GetString(4), reader.GetString(5), Text(reader, 6), CatalogValues.ParseTimestamp(reader.GetString(7)));

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
}
