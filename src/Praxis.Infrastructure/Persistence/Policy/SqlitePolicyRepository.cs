using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Policy;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;

namespace Praxis.Infrastructure.Persistence.Policy;

/// <summary>
/// Disclosure policy, categories, tiers, holds and the invocation audit. Every applied or
/// resolved policy change is retained in <c>policy_changes</c>; hold decisions requeue the job
/// that was waiting, inside the same transaction.
/// </summary>
public sealed class SqlitePolicyRepository : IPolicyRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqlitePolicyRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public DisclosurePolicy ReadPolicy() => database.ExecuteRead(connection =>
    {
        DisclosurePolicy policy = DisclosurePolicy.Default;
        using (SqliteCommand settings = connection.CreateCommand("SELECT mode, tool_permission, hold_hours FROM disclosure_settings WHERE singleton = 1"))
        using (SqliteDataReader reader = settings.ExecuteReader())
        {
            if (reader.Read())
            {
                policy = policy with { Mode = reader.GetString(0), ToolPermission = reader.GetString(1), HoldDuration = TimeSpan.FromHours(reader.GetInt32(2)) };
            }
        }

        List<DisclosureRule> rules = [];
        using (SqliteCommand command = connection.CreateCommand(
                   "SELECT rule_id, category, provider_id, purpose, allow, expires_utc FROM disclosure_rules ORDER BY created_utc, rule_id"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rules.Add(new DisclosureRule(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4) == 1, reader.IsDBNull(5) ? null : CatalogValues.ParseTimestamp(reader.GetString(5))));
            }
        }

        Dictionary<string, IReadOnlyList<string>> fallbacks = new(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand("SELECT category, chain FROM disclosure_fallbacks"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                fallbacks[reader.GetString(0)] = reader.GetString(1).Split(',');
            }
        }

        return policy with { Rules = rules, Fallbacks = fallbacks };
    });

    public void ApplyChange(PolicyChange change, string actor)
    {
        change.Validate();
        Actor(actor);
        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            string now = Now();
            Apply(connection, transaction, change, now);
            connection.Run(transaction, """
                INSERT INTO policy_changes (change_id, payload, requested_by, rationale, status, requested_utc, resolved_utc)
                VALUES ($id, $payload, $actor, '', 'applied', $now, $now)
                """, ("$id", Guid.NewGuid().ToString("D")), ("$payload", Serialize(change)), ("$actor", actor), ("$now", now));
            transaction.Commit();
        });
    }

    public Guid AddPendingChange(PolicyChange change, string actor, string rationale)
    {
        change.Validate();
        Actor(actor);
        Guid id = Guid.NewGuid();
        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO policy_changes (change_id, payload, requested_by, rationale, status, requested_utc)
                VALUES ($id, $payload, $actor, $rationale, 'pending', $now)
                """, ("$id", id.ToString("D")), ("$payload", Serialize(change)), ("$actor", actor), ("$rationale", rationale), ("$now", Now()));
            transaction.Commit();
        });
        return id;
    }

    public IReadOnlyList<PendingPolicyChange> ReadPendingChanges() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT change_id, payload, requested_by, rationale, requested_utc FROM policy_changes
            WHERE status = 'pending' ORDER BY requested_utc LIMIT 100
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        List<PendingPolicyChange> pending = [];
        while (reader.Read())
        {
            pending.Add(new PendingPolicyChange(Guid.Parse(reader.GetString(0)), Deserialize(reader.GetString(1)), reader.GetString(2),
                reader.GetString(3), CatalogValues.ParseTimestamp(reader.GetString(4))));
        }

        return pending;
    });

    public bool ResolvePendingChange(Guid changeId, bool confirm) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand select = connection.CreateCommand("SELECT payload FROM policy_changes WHERE change_id = $id AND status = 'pending'");
        select.Transaction = transaction;
        select.Parameters.AddWithValue("$id", changeId.ToString("D"));
        if (select.ExecuteScalar() is not string payload)
        {
            return false;
        }

        string now = Now();
        if (confirm)
        {
            Apply(connection, transaction, Deserialize(payload), now);
        }

        connection.Run(transaction, "UPDATE policy_changes SET status = $status, resolved_utc = $now WHERE change_id = $id",
            ("$status", confirm ? "applied" : "rejected"), ("$now", now), ("$id", changeId.ToString("D")));
        transaction.Commit();
        return true;
    });

    public SourceCategory? ReadCategory(Guid sourceId) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("SELECT category, origin, local_only FROM source_categories WHERE source_id = $source");
        command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new SourceCategory(sourceId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1) : null;
    });

    public void SaveCategory(SourceCategory category)
    {
        if (category.SourceId == Guid.Empty || !DataCategories.IsValid(category.Category) ||
            category.Origin is not (SourceCategory.HintOrigin or SourceCategory.ModelOrigin or SourceCategory.OwnerOrigin))
        {
            throw new ArgumentException("The source category is invalid.", nameof(category));
        }

        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO source_categories (source_id, category, origin, local_only, updated_utc)
                VALUES ($source, $category, $origin, $local, $now)
                ON CONFLICT(source_id) DO UPDATE SET category = excluded.category, origin = excluded.origin,
                    local_only = excluded.local_only, updated_utc = excluded.updated_utc
                WHERE source_categories.origin <> 'owner' OR excluded.origin = 'owner'
                """, ("$source", category.SourceId.ToString("D")), ("$category", category.Category), ("$origin", category.Origin),
                ("$local", category.LocalOnly ? 1 : 0), ("$now", Now()));
            transaction.Commit();
        });
    }

    public IReadOnlyList<TierAssignment> ReadTiers() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("SELECT tier, provider_id, model, effort FROM model_tiers ORDER BY tier");
        using SqliteDataReader reader = command.ExecuteReader();
        List<TierAssignment> tiers = [];
        while (reader.Read())
        {
            tiers.Add(new TierAssignment(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return tiers;
    });

    public void SaveTier(string tier, string? providerId, string? model = null, string? effort = null)
    {
        if (!ModelTiers.All.Contains(tier, StringComparer.Ordinal) || providerId?.Length is 0 or > 64 || model?.Length is 0 or > 100 ||
            !ModelEffort.IsValid(effort))
        {
            throw new ArgumentException("The tier assignment is invalid.");
        }

        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (providerId is null)
            {
                connection.Run(transaction, "DELETE FROM model_tiers WHERE tier = $tier", ("$tier", tier));
            }
            else
            {
                connection.Run(transaction, """
                    INSERT INTO model_tiers (tier, provider_id, model, effort, updated_utc) VALUES ($tier, $provider, $model, $effort, $now)
                    ON CONFLICT(tier) DO UPDATE SET provider_id = excluded.provider_id, model = excluded.model, effort = excluded.effort,
                        updated_utc = excluded.updated_utc
                    """, ("$tier", tier), ("$provider", providerId), ("$model", model), ("$effort", effort), ("$now", Now()));
            }

            transaction.Commit();
        });
    }

    public DisclosureHold? ReadHold(Guid itemId, int revision, string processorKey, string providerId) =>
        Holds("h.item_id = $item AND h.revision = $revision AND h.processor_key = $processor AND h.provider_id = $provider", 1,
            ("$item", itemId.ToString("D")), ("$revision", revision), ("$processor", processorKey), ("$provider", providerId)).FirstOrDefault();

    public Guid OpenHold(Guid itemId, int revision, string processorKey, string category, string providerId, string purpose, DateTimeOffset deadline)
    {
        Guid id = Guid.NewGuid();
        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO disclosure_holds (hold_id, item_id, revision, processor_key, category, provider_id, purpose, status, deadline_utc, created_utc)
                VALUES ($id, $item, $revision, $processor, $category, $provider, $purpose, 'open', $deadline, $now)
                """, ("$id", id.ToString("D")), ("$item", itemId.ToString("D")), ("$revision", revision), ("$processor", processorKey),
                ("$category", category), ("$provider", providerId), ("$purpose", purpose), ("$deadline", CatalogValues.Timestamp(deadline)), ("$now", Now()));
            transaction.Commit();
        });
        return id;
    }

    public IReadOnlyList<DisclosureHold> ReadHolds(bool openOnly, int limit) =>
        Holds(openOnly ? "h.status = 'open'" : "1 = 1", Math.Clamp(limit, 1, 500));

    public DisclosureHold? DecideHold(Guid holdId, string decision)
    {
        if (!HoldDecisions.All.Contains(decision, StringComparer.Ordinal))
        {
            throw new ArgumentException("The hold decision is not recognized.", nameof(decision));
        }

        bool approve = decision is HoldDecisions.ApproveOnce or HoldDecisions.GrantCategory;
        bool decided = database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            string now = Now();
            using SqliteCommand select = connection.CreateCommand("""
                SELECT item_id, revision, processor_key, category, provider_id, purpose FROM disclosure_holds WHERE hold_id = $id AND status = 'open'
                """);
            select.Transaction = transaction;
            select.Parameters.AddWithValue("$id", holdId.ToString("D"));
            string item, processor, category, provider, purpose;
            int revision;
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                (item, revision, processor, category, provider, purpose) =
                    (reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
            }

            connection.Run(transaction, "UPDATE disclosure_holds SET status = $status, decided_utc = $now WHERE hold_id = $id",
                ("$status", approve ? DisclosureHold.ApprovedOnce : DisclosureHold.Declined), ("$now", now), ("$id", holdId.ToString("D")));
            if (decision == HoldDecisions.GrantCategory)
            {
                PolicyChange grant = new(AddRule: new DisclosureRule(Guid.NewGuid(), category, provider, purpose, true, null));
                Apply(connection, transaction, grant, now);
                connection.Run(transaction, """
                    INSERT INTO policy_changes (change_id, payload, requested_by, rationale, status, requested_utc, resolved_utc)
                    VALUES ($change, $payload, 'owner', 'granted from a disclosure hold', 'applied', $now, $now)
                    """, ("$change", Guid.NewGuid().ToString("D")), ("$payload", Serialize(grant)), ("$now", now));
            }

            Requeue(connection, transaction, item, revision, processor, now);
            transaction.Commit();
            return true;
        });
        return decided ? Holds("h.hold_id = $id", 1, ("$id", holdId.ToString("D"))).FirstOrDefault() : null;
    }

    public int ExpireHolds(DateTimeOffset now) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string time = CatalogValues.Timestamp(now);
        using SqliteCommand select = connection.CreateCommand("""
            SELECT hold_id, item_id, revision, processor_key FROM disclosure_holds WHERE status = 'open' AND deadline_utc <= $now LIMIT 200
            """);
        select.Transaction = transaction;
        select.Parameters.AddWithValue("$now", time);
        List<(string Hold, string Item, int Revision, string Processor)> expired = [];
        using (SqliteDataReader reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                expired.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
            }
        }

        foreach ((string hold, string item, int revision, string processor) in expired)
        {
            connection.Run(transaction, "UPDATE disclosure_holds SET status = 'expired', decided_utc = $now WHERE hold_id = $id", ("$now", time), ("$id", hold));
            Requeue(connection, transaction, item, revision, processor, time);
        }

        transaction.Commit();
        return expired.Count;
    });

    public void RecordInvocation(ModelInvocation invocation) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        connection.Run(transaction, """
            INSERT INTO model_invocations (tier, provider_id, remote, purpose, category, decision_reason, item_id, revision, processor_key,
                model_name, input_tokens, output_tokens, outcome, started_utc, duration_ms)
            VALUES ($tier, $provider, $remote, $purpose, $category, $reason, $item, $revision, $processor, $model, $input, $output, $outcome, $started, $duration)
            """, ("$tier", invocation.Tier), ("$provider", invocation.ProviderId), ("$remote", invocation.Remote ? 1 : 0), ("$purpose", invocation.Purpose),
            ("$category", invocation.Category), ("$reason", invocation.DecisionReason), ("$item", invocation.ItemId?.ToString("D")),
            ("$revision", invocation.Revision), ("$processor", invocation.ProcessorKey),
            ("$model", invocation.ModelName is { Length: > 200 } name ? name[..200] : invocation.ModelName),
            ("$input", Math.Max(0, invocation.InputTokens)), ("$output", Math.Max(0, invocation.OutputTokens)), ("$outcome", invocation.Outcome),
            ("$started", CatalogValues.Timestamp(invocation.StartedUtc)), ("$duration", invocation.DurationMs is >= 0 ? invocation.DurationMs : null));
        transaction.Commit();
    });

    public int InvocationsToday(string providerId, DateTimeOffset now) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("SELECT COUNT(*) FROM model_invocations WHERE provider_id = $provider AND started_utc >= $day");
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$day", CatalogValues.Timestamp(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)));
        return Convert.ToInt32(command.ExecuteScalar());
    });

    public IReadOnlyList<ModelInvocation> ReadInvocations(int limit) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT tier, provider_id, remote, purpose, category, decision_reason, item_id, revision, processor_key, model_name,
                   input_tokens, output_tokens, outcome, started_utc, duration_ms
            FROM model_invocations ORDER BY invocation_id DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1_000));
        using SqliteDataReader reader = command.ExecuteReader();
        List<ModelInvocation> invocations = [];
        while (reader.Read())
        {
            invocations.Add(new ModelInvocation(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1, reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetString(12), CatalogValues.ParseTimestamp(reader.GetString(13)), reader.IsDBNull(14) ? null : reader.GetInt32(14)));
        }

        return invocations;
    });

    private IReadOnlyList<DisclosureHold> Holds(string condition, int limit, params (string Name, object? Value)[] parameters) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT h.hold_id, s.source_id, s.locator, h.item_id, h.revision, h.processor_key, h.category, h.provider_id, h.purpose,
                   h.status, h.deadline_utc, h.created_utc
            FROM disclosure_holds h
            JOIN items i ON i.item_id = h.item_id
            JOIN sources s ON s.source_id = i.source_id
            WHERE {condition}
            ORDER BY h.created_utc DESC, h.hold_id
            LIMIT {limit}
            """);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        List<DisclosureHold> holds = [];
        while (reader.Read())
        {
            holds.Add(new DisclosureHold(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Path.GetFileName(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), CatalogValues.ParseTimestamp(reader.GetString(10)), CatalogValues.ParseTimestamp(reader.GetString(11))));
        }

        return holds;
    });

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction, PolicyChange change, string now)
    {
        change.Validate();
        if (change.Mode is not null || change.ToolPermission is not null)
        {
            connection.Run(transaction, """
                INSERT INTO disclosure_settings (singleton, mode, tool_permission, hold_hours, updated_utc)
                VALUES (1, COALESCE($mode, 'standing_grants'), COALESCE($tool, 'read_only'), 72, $now)
                ON CONFLICT(singleton) DO UPDATE SET mode = COALESCE($mode, mode), tool_permission = COALESCE($tool, tool_permission), updated_utc = $now
                """, ("$mode", change.Mode), ("$tool", change.ToolPermission), ("$now", now));
        }
        else if (change.AddRule is { } rule)
        {
            connection.Run(transaction, """
                INSERT INTO disclosure_rules (rule_id, category, provider_id, purpose, allow, expires_utc, created_utc)
                VALUES ($id, $category, $provider, $purpose, $allow, $expires, $now)
                """, ("$id", rule.RuleId.ToString("D")), ("$category", rule.Category), ("$provider", rule.ProviderId), ("$purpose", rule.Purpose),
                ("$allow", rule.Allow ? 1 : 0), ("$expires", rule.ExpiresUtc is { } expires ? CatalogValues.Timestamp(expires) : null), ("$now", now));
        }
        else if (change.RemoveRuleId is { } id)
        {
            connection.Run(transaction, "DELETE FROM disclosure_rules WHERE rule_id = $id", ("$id", id.ToString("D")));
        }
        else
        {
            connection.Run(transaction, """
                INSERT INTO disclosure_fallbacks (category, chain, updated_utc) VALUES ($category, $chain, $now)
                ON CONFLICT(category) DO UPDATE SET chain = excluded.chain, updated_utc = excluded.updated_utc
                """, ("$category", change.FallbackCategory), ("$chain", string.Join(',', change.FallbackChain!)), ("$now", now));
        }
    }

    private static void Requeue(SqliteConnection connection, SqliteTransaction transaction, string item, int revision, string processor, string now) =>
        connection.Run(transaction, """
            UPDATE processing_jobs
            SET state = 'queued', attempt = 0, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
            WHERE item_id = $item AND revision = $revision AND processor_key = $processor
              AND state IN ('completed', 'unsupported', 'failed')
            """, ("$now", now), ("$item", item), ("$revision", revision), ("$processor", processor));

    private static void Actor(string actor)
    {
        if (actor is not (PolicyActors.Owner or PolicyActors.Agent))
        {
            throw new ArgumentException("The policy actor is not recognized.", nameof(actor));
        }
    }

    private static string Serialize(PolicyChange change) => JsonSerializer.Serialize(change, Json);

    private static PolicyChange Deserialize(string payload) =>
        JsonSerializer.Deserialize<PolicyChange>(payload, Json) ?? throw new InvalidDataException("A stored policy change is unreadable.");

    private string Now() => CatalogValues.Timestamp(clock.GetUtcNow());
}
