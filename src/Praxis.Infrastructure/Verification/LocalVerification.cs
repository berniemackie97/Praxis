using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Knowledge;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Verification;

/// <summary>
/// Owner-approved verification of local results by a stronger model. Requests are planned from
/// deterministic signals (instruction-like evidence, entity type conflicts) or asked for by the
/// owner, wait for approval with their exact payload visible, and are routed to the cheapest
/// enabled provider with remaining daily allowance. Verdicts act on the machine layer: they can
/// hide or release proposals and set verified entity types, but never override owner review.
/// </summary>
public sealed class LocalVerification
{
    public const int MaximumItemsPerRequest = 12;
    public const int MaximumAwaitingRequests = 20;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);
    private readonly CatalogDatabase database;

    internal LocalVerification(CatalogDatabase database) => this.database = database;

    public IReadOnlyList<VerificationProvider> Providers() => database.Execute(connection =>
    {
        using var query = connection.CreateCommand("""
            SELECT provider_id, executable, model, cost_weight, daily_limit, enabled
            FROM verification_providers ORDER BY cost_weight, provider_id
            """);
        using var reader = query.ExecuteReader();
        var providers = new List<VerificationProvider>();
        while (reader.Read())
        {
            providers.Add(new VerificationProvider(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.GetInt64(5) == 1));
        }

        return providers;
    });

    public VerificationProvider SaveProvider(VerificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.ProviderId is not (VerificationProvider.Codex or VerificationProvider.ClaudeCode))
        {
            throw new ArgumentException("The verification provider is not supported.", nameof(provider));
        }

        if (provider.CostWeight is < 0 or > 1000 || provider.DailyLimit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "Weights are 0–1000 and daily limits 1–500.");
        }

        // Constructing the adapter validates the executable path and model name.
        _ = ProviderModels.Create(provider);
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO verification_providers
                    (provider_id, executable, model, cost_weight, daily_limit, enabled, updated_utc)
                VALUES ($id, $executable, $model, $weight, $limit, $enabled, $now)
                ON CONFLICT(provider_id) DO UPDATE SET executable = excluded.executable,
                    model = excluded.model, cost_weight = excluded.cost_weight,
                    daily_limit = excluded.daily_limit, enabled = excluded.enabled,
                    updated_utc = excluded.updated_utc
                """, ("$id", provider.ProviderId), ("$executable", provider.Executable), ("$model", provider.Model),
                ("$weight", provider.CostWeight), ("$limit", provider.DailyLimit),
                ("$enabled", provider.Enabled ? 1 : 0), ("$now", now));
            // Saving a provider is the owner's signal that it is ready again (for example, after
            // signing in), so requests waiting on it are retried now instead of at their delay.
            connection.Run(transaction, """
                UPDATE verification_requests SET retry_after_utc = NULL
                WHERE provider_id = $id AND status = 'approved'
                """, ("$id", provider.ProviderId));
            transaction.Commit();
        });
        return provider;
    }

    /// <summary>
    /// Creates requests awaiting approval for unverified instruction-like proposals and for
    /// unresolved entity type conflicts. Items already in a request (other than a failed one)
    /// are not planned again; declining a request therefore silences its items.
    /// </summary>
    public int Plan()
    {
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var awaiting = Count(connection, transaction,
                "SELECT COUNT(*) FROM verification_requests WHERE status = 'awaiting_approval'");
            var created = 0;
            if (awaiting < MaximumAwaitingRequests)
            {
                var flagged = Ids(connection, transaction, """
                    SELECT p.proposal_id FROM eligible_enrichment_proposals p
                    WHERE p.flags = 'instruction_like' AND p.status = 'pending' AND p.kind IN ('entity', 'relation')
                      AND NOT EXISTS (SELECT 1 FROM verification_items i
                          JOIN verification_requests r ON r.request_id = i.request_id
                          WHERE i.proposal_id = p.proposal_id AND r.status <> 'failed')
                    ORDER BY p.created_utc, p.proposal_id LIMIT $limit
                    """);
                created += Create(connection, transaction, "instruction_like", flagged, [], now) ? 1 : 0;
            }

            if (awaiting + created < MaximumAwaitingRequests)
            {
                var conflicts = Ids(connection, transaction, """
                    SELECT e.entity_id FROM knowledge_entities e
                    JOIN visible_entity_mentions m ON m.entity_id = e.entity_id
                    WHERE e.status = 'active' AND e.type_origin = 'inferred'
                      AND NOT EXISTS (SELECT 1 FROM verification_items i
                          JOIN verification_requests r ON r.request_id = i.request_id
                          WHERE i.entity_id = e.entity_id AND r.status <> 'failed')
                    GROUP BY e.entity_id HAVING COUNT(DISTINCT m.entity_type) > 1
                    ORDER BY COUNT(*) DESC, e.entity_id LIMIT $limit
                    """);
                created += Create(connection, transaction, "type_conflict", [], conflicts, now) ? 1 : 0;
            }

            transaction.Commit();
            return created;
        });
    }

    /// <summary>Owner request to verify a source's current entity and relation proposals.</summary>
    public Guid? RequestForSource(Guid sourceId)
    {
        RequireId(sourceId);
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var query = connection.CreateCommand("""
                SELECT proposal_id FROM eligible_enrichment_proposals
                WHERE source_id = $source AND kind IN ('entity', 'relation') AND status = 'pending'
                ORDER BY evidence_offset, proposal_id LIMIT $limit
                """);
            query.Transaction = transaction;
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", MaximumItemsPerRequest);
            var proposals = ReadIds(query);
            var request = Create(connection, transaction, "owner_request", proposals, [], now, out var id) ? id : (Guid?)null;
            transaction.Commit();
            return request;
        });
    }

    /// <summary>Owner request to settle an entity's type with a stronger model.</summary>
    public Guid? RequestForEntity(Guid entityId)
    {
        RequireId(entityId);
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var request = Create(connection, transaction, "owner_request", [], [entityId.ToString("D")], now, out var id)
                ? id : (Guid?)null;
            transaction.Commit();
            return request;
        });
    }

    public IReadOnlyList<VerificationRequestView> List(int limit = 50)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.Execute(connection =>
        {
            using var query = connection.CreateCommand(
                "SELECT request_id FROM verification_requests ORDER BY created_utc DESC, request_id LIMIT $limit");
            query.Parameters.AddWithValue("$limit", limit);
            return ReadIds(query).Select(id => Read(connection, null, id)!).ToArray();
        });
    }

    public VerificationRequestView? Get(Guid requestId)
    {
        RequireId(requestId);
        return database.Execute(connection => Read(connection, null, requestId.ToString("D")));
    }

    /// <summary>
    /// Approves a request after the owner has reviewed its payload: excluded items are removed,
    /// phrases are redacted, and the request is routed to <paramref name="providerId"/> or the
    /// cheapest enabled provider with remaining daily allowance.
    /// </summary>
    public VerificationRequestView Approve(
        Guid requestId, string? providerId = null, IReadOnlyCollection<int>? excludedOrdinals = null,
        IReadOnlyCollection<string>? redactions = null)
    {
        RequireId(requestId);
        var now = DateTimeOffset.UtcNow;
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var id = requestId.ToString("D");
            using var request = connection.CreateCommand(
                "SELECT payload FROM verification_requests WHERE request_id = $id AND status = 'awaiting_approval'");
            request.Transaction = transaction;
            request.Parameters.AddWithValue("$id", id);
            if (request.ExecuteScalar() is not string payload)
            {
                throw new InvalidOperationException("Only a request awaiting approval can be approved.");
            }

            var provider = Route(connection, transaction, providerId, now)
                ?? throw new InvalidOperationException("No enabled verifier has allowance left today.");
            var (narrowed, kept) = VerificationPayload.Narrow(payload, excludedOrdinals ?? [], redactions ?? []);
            connection.Run(transaction, """
                UPDATE verification_items SET included = 0 WHERE request_id = $id
                """, ("$id", id));
            foreach (var ordinal in kept)
            {
                connection.Run(transaction,
                    "UPDATE verification_items SET included = 1 WHERE request_id = $id AND ordinal = $ordinal",
                    ("$id", id), ("$ordinal", ordinal));
            }

            connection.Run(transaction, """
                UPDATE verification_requests SET status = 'approved', provider_id = $provider, payload = $payload,
                    decided_utc = $now, retry_after_utc = NULL WHERE request_id = $id
                """, ("$provider", provider.ProviderId), ("$payload", narrowed),
                ("$now", CatalogValues.Timestamp(now)), ("$id", id));
            transaction.Commit();
            return Read(connection, null, id)!;
        });
    }

    public bool Decline(Guid requestId)
    {
        RequireId(requestId);
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var changed = connection.Run(transaction, """
                UPDATE verification_requests SET status = 'declined', decided_utc = $now
                WHERE request_id = $id AND status IN ('awaiting_approval', 'approved')
                """, ("$now", now), ("$id", requestId.ToString("D"))) == 1;
            transaction.Commit();
            return changed;
        });
    }

    /// <summary>Leases the oldest approved request whose provider is enabled and not backed off.</summary>
    internal ClaimedVerification? Claim(DateTimeOffset now) => database.Execute(connection =>
    {
        var time = CatalogValues.Timestamp(now);
        using var transaction = connection.BeginTransaction();
        using var query = connection.CreateCommand("""
            SELECT r.request_id, r.payload, p.provider_id, p.executable, p.model, p.cost_weight, p.daily_limit
            FROM verification_requests r
            JOIN verification_providers p ON p.provider_id = r.provider_id AND p.enabled = 1
            WHERE (r.status = 'approved' AND (r.retry_after_utc IS NULL OR r.retry_after_utc <= $now))
               OR (r.status = 'running' AND r.lease_until_utc <= $now)
            ORDER BY r.decided_utc, r.request_id LIMIT 20
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$now", time);
        var candidates = new List<(string Id, string Payload, VerificationProvider Provider)>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), new VerificationProvider(reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5),
                    reader.GetInt32(6), true)));
            }
        }

        // The oldest request whose provider still has allowance today; others wait for tomorrow.
        var next = candidates.FirstOrDefault(candidate =>
            StartedToday(connection, transaction, candidate.Provider.ProviderId, now) < candidate.Provider.DailyLimit);
        if (next.Id is null)
        {
            return null;
        }

        var (id, payload, provider) = next;
        var lease = Guid.NewGuid();
        connection.Run(transaction, """
            UPDATE verification_requests SET status = 'running', lease_key = $lease, lease_until_utc = $until,
                started_utc = $now WHERE request_id = $id
            """, ("$lease", lease.ToString("D")), ("$until", CatalogValues.Timestamp(now + LeaseDuration)),
            ("$now", time), ("$id", id));
        var ordinals = Ordinals(connection, transaction, id);
        transaction.Commit();
        return new ClaimedVerification(Guid.Parse(id), lease, provider, payload, ordinals);
    });

    /// <summary>
    /// Records verdicts and their machine-layer effects atomically: item verdicts, and verified
    /// types for entities whose type the owner has not set.
    /// </summary>
    internal bool Complete(
        ClaimedVerification claim, ModelIdentity model, ChatResult result, IReadOnlyList<VerificationVerdict> verdicts)
    {
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var id = claim.RequestId.ToString("D");
            if (!HoldsLease(connection, transaction, claim))
            {
                return false;
            }

            var affected = new List<string>();
            foreach (var verdict in verdicts)
            {
                connection.Run(transaction, """
                    UPDATE verification_items SET verdict = $verdict, corrected_type = $type, note = $note
                    WHERE request_id = $id AND ordinal = $ordinal AND included = 1
                    """, ("$verdict", verdict.Verdict), ("$type", verdict.CorrectedType), ("$note", verdict.Note),
                    ("$id", id), ("$ordinal", verdict.Ordinal));
                if (TargetEntity(connection, transaction, id, verdict.Ordinal) is not { } entity)
                {
                    continue;
                }

                affected.Add(entity);
                if (verdict.CorrectedType is { } type)
                {
                    connection.Run(transaction, """
                        UPDATE knowledge_entities SET entity_type = $type, type_origin = 'verified', updated_utc = $now
                        WHERE entity_id = $entity AND type_origin <> 'owner'
                        """, ("$type", type), ("$now", now), ("$entity", entity));
                }
            }

            connection.Run(transaction, """
                UPDATE verification_requests SET status = 'completed', completed_utc = $now, lease_key = NULL,
                    lease_until_utc = NULL, failure_code = NULL, model_name = $model, model_version = $version,
                    input_tokens = $input, output_tokens = $output, duration_ms = $duration
                WHERE request_id = $id
                """, ("$now", now), ("$model", model.Model), ("$version", model.Digest),
                ("$input", result.InputTokens), ("$output", result.OutputTokens),
                ("$duration", (long)result.Duration.TotalMilliseconds), ("$id", id));
            KnowledgeLinker.RecomputeInferredTypes(connection, transaction, affected, now);
            transaction.Commit();
            return true;
        });
    }

    /// <summary>Returns a running request to the approved queue until <paramref name="retryAfter"/>.</summary>
    internal bool Release(ClaimedVerification claim, DateTimeOffset retryAfter, string failureCode) =>
        Finish(claim, """
            UPDATE verification_requests SET status = 'approved', lease_key = NULL, lease_until_utc = NULL,
                retry_after_utc = $retry, failure_code = $code WHERE request_id = $id
            """, ("$retry", CatalogValues.Timestamp(retryAfter)), ("$code", failureCode));

    internal bool Fail(ClaimedVerification claim, string failureCode) =>
        Finish(claim, """
            UPDATE verification_requests SET status = 'failed', lease_key = NULL, lease_until_utc = NULL,
                completed_utc = $now, failure_code = $code WHERE request_id = $id
            """, ("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow)), ("$code", failureCode));

    private bool Finish(ClaimedVerification claim, string sql, params (string Name, object? Value)[] parameters) =>
        database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            if (!HoldsLease(connection, transaction, claim))
            {
                return false;
            }

            connection.Run(transaction, sql, [.. parameters, ("$id", claim.RequestId.ToString("D"))]);
            transaction.Commit();
            return true;
        });

    private static bool Create(
        SqliteConnection connection, SqliteTransaction transaction, string reason,
        IReadOnlyList<string> proposals, IReadOnlyList<string> entities, string now) =>
        Create(connection, transaction, reason, proposals, entities, now, out _);

    private static bool Create(
        SqliteConnection connection, SqliteTransaction transaction, string reason,
        IReadOnlyList<string> proposals, IReadOnlyList<string> entities, string now, out Guid requestId)
    {
        requestId = Guid.NewGuid();
        var items = new List<(JsonObject Item, string? Proposal, string? Entity)>();
        foreach (var proposal in proposals.Take(MaximumItemsPerRequest))
        {
            if (VerificationEvidence.ForProposal(connection, transaction, items.Count + 1, proposal) is { } item)
            {
                items.Add((item, proposal, null));
            }
        }

        foreach (var entity in entities.Take(MaximumItemsPerRequest - items.Count))
        {
            if (VerificationEvidence.ForEntityType(connection, transaction, items.Count + 1, entity) is { } item)
            {
                items.Add((item, null, entity));
            }
        }

        if (items.Count == 0)
        {
            return false;
        }

        var id = requestId.ToString("D");
        connection.Run(transaction, """
            INSERT INTO verification_requests (request_id, reason, status, payload, created_utc)
            VALUES ($id, $reason, 'awaiting_approval', $payload, $now)
            """, ("$id", id), ("$reason", reason),
            ("$payload", VerificationPayload.Serialize(items.Select(entry => entry.Item).ToArray())), ("$now", now));
        for (var index = 0; index < items.Count; index++)
        {
            connection.Run(transaction, """
                INSERT INTO verification_items (request_id, ordinal, proposal_id, entity_id, included)
                VALUES ($id, $ordinal, $proposal, $entity, 1)
                """, ("$id", id), ("$ordinal", index + 1), ("$proposal", items[index].Proposal),
                ("$entity", items[index].Entity));
        }

        return true;
    }

    /// <summary>The requested provider, or the cheapest enabled one with allowance left today.</summary>
    private static VerificationProvider? Route(
        SqliteConnection connection, SqliteTransaction transaction, string? providerId, DateTimeOffset now)
    {
        using var query = connection.CreateCommand("""
            SELECT provider_id, executable, model, cost_weight, daily_limit FROM verification_providers
            WHERE enabled = 1 AND ($provider IS NULL OR provider_id = $provider)
            ORDER BY cost_weight, provider_id
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$provider", (object?)providerId ?? DBNull.Value);
        var candidates = new List<VerificationProvider>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add(new VerificationProvider(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), true));
            }
        }

        return candidates.FirstOrDefault(provider =>
            StartedToday(connection, transaction, provider.ProviderId, now) < provider.DailyLimit);
    }

    private static int StartedToday(
        SqliteConnection connection, SqliteTransaction transaction, string providerId, DateTimeOffset now)
    {
        using var query = connection.CreateCommand("""
            SELECT COUNT(*) FROM verification_requests
            WHERE provider_id = $provider AND started_utc >= $day
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$provider", providerId);
        query.Parameters.AddWithValue("$day", CatalogValues.Timestamp(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)));
        return Convert.ToInt32(query.ExecuteScalar());
    }

    private static bool HoldsLease(SqliteConnection connection, SqliteTransaction transaction, ClaimedVerification claim)
    {
        using var query = connection.CreateCommand("""
            SELECT 1 FROM verification_requests
            WHERE request_id = $id AND status = 'running' AND lease_key = $lease
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", claim.RequestId.ToString("D"));
        query.Parameters.AddWithValue("$lease", claim.LeaseKey.ToString("D"));
        return query.ExecuteScalar() is not null;
    }

    /// <summary>The entity an item settles: its own entity, or the entity a proposal mention links to.</summary>
    private static string? TargetEntity(SqliteConnection connection, SqliteTransaction transaction, string request, int ordinal)
    {
        using var query = connection.CreateCommand("""
            SELECT COALESCE(i.entity_id, m.entity_id) FROM verification_items i
            LEFT JOIN entity_mentions m ON m.proposal_id = i.proposal_id
            WHERE i.request_id = $id AND i.ordinal = $ordinal
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", request);
        query.Parameters.AddWithValue("$ordinal", ordinal);
        return query.ExecuteScalar() as string;
    }

    private static IReadOnlySet<int> Ordinals(SqliteConnection connection, SqliteTransaction transaction, string request)
    {
        using var query = connection.CreateCommand(
            "SELECT ordinal FROM verification_items WHERE request_id = $id AND included = 1");
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", request);
        using var reader = query.ExecuteReader();
        var ordinals = new HashSet<int>();
        while (reader.Read())
        {
            ordinals.Add(reader.GetInt32(0));
        }

        return ordinals;
    }

    private static VerificationRequestView? Read(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var request = connection.CreateCommand("""
            SELECT reason, status, provider_id, created_utc, failure_code, model_name, input_tokens, output_tokens, payload
            FROM verification_requests WHERE request_id = $id
            """);
        request.Transaction = transaction;
        request.Parameters.AddWithValue("$id", id);
        using var reader = request.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var header = (Reason: reader.GetString(0), Status: reader.GetString(1),
            Provider: reader.IsDBNull(2) ? null : reader.GetString(2), Created: CatalogValues.ParseTimestamp(reader.GetString(3)),
            Failure: reader.IsDBNull(4) ? null : reader.GetString(4), Model: reader.IsDBNull(5) ? null : reader.GetString(5),
            Input: reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6), Output: reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
            Payload: reader.GetString(8));
        using var items = connection.CreateCommand("""
            SELECT ordinal, proposal_id, entity_id, included, verdict, corrected_type, note
            FROM verification_items WHERE request_id = $id ORDER BY ordinal
            """);
        items.Transaction = transaction;
        items.Parameters.AddWithValue("$id", id);
        using var itemReader = items.ExecuteReader();
        var views = new List<VerificationItemView>();
        while (itemReader.Read())
        {
            views.Add(new VerificationItemView(itemReader.GetInt32(0),
                itemReader.IsDBNull(1) ? null : Guid.Parse(itemReader.GetString(1)),
                itemReader.IsDBNull(2) ? null : Guid.Parse(itemReader.GetString(2)),
                itemReader.GetInt64(3) == 1, itemReader.IsDBNull(4) ? null : itemReader.GetString(4),
                itemReader.IsDBNull(5) ? null : itemReader.GetString(5), itemReader.IsDBNull(6) ? null : itemReader.GetString(6)));
        }

        return new VerificationRequestView(Guid.Parse(id), header.Reason, header.Status, header.Provider, header.Created,
            header.Failure, header.Model, header.Input, header.Output, header.Payload, views);
    }

    private static int Count(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var query = connection.CreateCommand(sql);
        query.Transaction = transaction;
        return Convert.ToInt32(query.ExecuteScalar());
    }

    private static List<string> Ids(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var query = connection.CreateCommand(sql);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$limit", MaximumItemsPerRequest);
        return ReadIds(query);
    }

    private static List<string> ReadIds(SqliteCommand query)
    {
        using var reader = query.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", nameof(id));
        }
    }
}
