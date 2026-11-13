using Microsoft.Data.Sqlite;
using Praxis.Domain.Verification;

namespace Praxis.Infrastructure.Persistence.Verification;

public sealed partial class SqliteVerificationRepository
{
    private static readonly TimeSpan DisclosureLifetime = TimeSpan.FromMinutes(15);

    public IReadOnlyList<VerificationDisclosureSource> DisclosureSources(Guid requestId)
    {
        RequireId(requestId);
        return database.ExecuteRead(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT evidence.source_id, s.locator, COALESCE(p.classification, 'local_only'), evidence.ordinal
                FROM (
                    SELECT i.ordinal, source.source_id FROM verification_items i
                    JOIN enrichment_proposals source ON source.proposal_id = i.proposal_id
                    WHERE i.request_id = $request
                    UNION
                    SELECT c.ordinal, c.source_id FROM verification_contexts c
                    WHERE c.request_id = $request
                ) evidence
                JOIN sources s ON s.source_id = evidence.source_id
                LEFT JOIN source_verification_policy p ON p.source_id = evidence.source_id
                ORDER BY evidence.source_id, evidence.ordinal
                """);
            query.Parameters.AddWithValue("$request", requestId.ToString("D"));
            using var reader = query.ExecuteReader();
            var sources = new List<VerificationDisclosureSource>();
            string? current = null;
            string? name = null;
            string? classification = null;
            var ordinals = new List<int>();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                if (current is not null && current != id)
                {
                    sources.Add(new VerificationDisclosureSource(Guid.Parse(current), name!, classification!, ordinals.ToArray()));
                    ordinals.Clear();
                }

                current = id;
                name = Path.GetFileName(reader.GetString(1));
                classification = reader.GetString(2);
                ordinals.Add(reader.GetInt32(3));
            }

            if (current is not null)
            {
                sources.Add(new VerificationDisclosureSource(Guid.Parse(current), name!, classification!, ordinals.ToArray()));
            }

            return sources;
        });
    }

    public bool SetSourceRemoteVerification(Guid sourceId, bool allowed)
    {
        RequireId(sourceId);
        string id = sourceId.ToString("D");
        string classification = allowed ? "remote_verification" : "local_only";
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var source = connection.CreateCommand("""
                SELECT status FROM sources WHERE source_id = $source
                """);
            source.Transaction = transaction;
            source.Parameters.AddWithValue("$source", id);
            if (source.ExecuteScalar() is not string status || status != "active")
            {
                return false;
            }

            int changed = connection.Run(transaction, """
                INSERT INTO source_verification_policy (source_id, classification, revision, changed_utc)
                VALUES ($source, $class, 1, $now)
                ON CONFLICT(source_id) DO UPDATE SET classification = excluded.classification,
                    revision = source_verification_policy.revision + 1, changed_utc = excluded.changed_utc
                WHERE source_verification_policy.classification <> excluded.classification
                """, ("$source", id), ("$class", classification), ("$now", now));
            if (changed == 0)
            {
                return false;
            }

            // Every existing consent for this source becomes invalid across classification
            // changes. Re-enabling it cannot resurrect an earlier grant.
            connection.Run(transaction, """
                UPDATE verification_disclosure_grants SET revoked_utc = $now
                WHERE revoked_utc IS NULL AND request_id IN (
                    SELECT request_id FROM verification_disclosure_sources WHERE source_id = $source)
                """, ("$now", now), ("$source", id));
            connection.Run(transaction, """
                UPDATE verification_invocations
                SET status = 'failed', finished_utc = $now, failure_code = 'disclosure_revoked'
                WHERE status = 'running' AND request_id IN (
                    SELECT request_id FROM verification_disclosure_sources WHERE source_id = $source)
                """, ("$now", now), ("$source", id));
            connection.Run(transaction, """
                UPDATE verification_requests
                SET status = 'failed', completed_utc = $now, failure_code = 'disclosure_revoked',
                    lease_key = NULL, lease_until_utc = NULL, retry_after_utc = NULL
                WHERE status IN ('approved', 'running') AND request_id IN (
                    SELECT request_id FROM verification_disclosure_sources WHERE source_id = $source)
                """, ("$now", now), ("$source", id));
            Audit(connection, transaction, null, id, null, "classification", "allowed",
                allowed ? "remote_verification" : "local_only", now);
            transaction.Commit();
            return true;
        });
    }

    public bool RevokeDisclosure(Guid requestId)
    {
        RequireId(requestId);
        string id = requestId.ToString("D");
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            int changed = connection.Run(transaction, """
                UPDATE verification_disclosure_grants SET revoked_utc = $now
                WHERE request_id = $id AND revoked_utc IS NULL
                """, ("$now", now), ("$id", id));
            if (changed == 0)
            {
                return false;
            }

            DenyDisclosure(connection, transaction, id, now, "disclosure_revoked");
            Audit(connection, transaction, id, null, null, "revocation", "allowed", "owner_revoked", now);
            transaction.Commit();
            return true;
        });
    }

    public bool CanDispatch(ClaimedVerification claim)
    {
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            string id = claim.RequestId.ToString("D");
            if (!HoldsLease(connection, transaction, claim))
            {
                return false;
            }

            if (!EvidenceIsCurrent(connection, transaction, id))
            {
                RejectStale(connection, transaction, id, now);
                transaction.Commit();
                return false;
            }

            if (!claim.Provider.IsLocal && !RemoteScopeValid(connection, transaction, id,
                    claim.Provider.ProviderId, claim.Provider.Revision, now))
            {
                DenyDisclosure(connection, transaction, id, now, "disclosure_denied");
                transaction.Commit();
                return false;
            }

            if (!claim.Provider.IsLocal)
            {
                Audit(connection, transaction, id, null, claim.Provider.ProviderId,
                    "dispatch", "allowed", "scope_current", now);
            }

            transaction.Commit();
            return true;
        });
    }

    public IReadOnlyList<VerificationDisclosureEvent> DisclosureAudit(int limit)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT event_id, request_id, source_id, provider_id, operation, result,
                       reason_code, occurred_utc
                FROM verification_disclosure_audit ORDER BY occurred_utc DESC, event_id DESC LIMIT $limit
                """);
            query.Parameters.AddWithValue("$limit", limit);
            using var reader = query.ExecuteReader();
            var events = new List<VerificationDisclosureEvent>();
            while (reader.Read())
            {
                events.Add(new VerificationDisclosureEvent(Guid.Parse(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    CatalogValues.ParseTimestamp(reader.GetString(7))));
            }

            return events;
        });
    }

    private static HashSet<string> IncludedSources(
        SqliteConnection connection, SqliteTransaction transaction, string request)
    {
        using var query = connection.CreateCommand("""
            SELECT p.source_id FROM verification_items i
            JOIN eligible_enrichment_proposals p ON p.proposal_id = i.proposal_id
            WHERE i.request_id = $request AND i.included = 1
            UNION
            SELECT c.source_id FROM verification_contexts c
            JOIN verification_items i ON i.request_id = c.request_id AND i.ordinal = c.ordinal
            WHERE i.request_id = $request AND i.included = 1
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$request", request);
        using var reader = query.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static Dictionary<string, int> EligibleRemoteSources(
        SqliteConnection connection, SqliteTransaction transaction, HashSet<string> required)
    {
        var policies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string source in required)
        {
            using var query = connection.CreateCommand("""
                SELECT p.revision FROM source_verification_policy p
                JOIN sources s ON s.source_id = p.source_id
                WHERE p.source_id = $source AND p.classification = 'remote_verification'
                  AND s.status = 'active'
                """);
            query.Transaction = transaction;
            query.Parameters.AddWithValue("$source", source);
            if (query.ExecuteScalar() is not long revision)
            {
                return [];
            }

            policies.Add(source, checked((int)revision));
        }

        return policies;
    }

    private bool GrantRemoteDisclosure(
        SqliteConnection connection, SqliteTransaction transaction, string request,
        string provider, int revision, string now)
    {
        HashSet<string> required = IncludedSources(connection, transaction, request);
        if (required.Count == 0)
        {
            return false;
        }

        Dictionary<string, int> policies = EligibleRemoteSources(connection, transaction, required);
        if (policies.Count != required.Count)
        {
            return false;
        }

        connection.Run(transaction, """
            INSERT INTO verification_disclosure_grants
                (request_id, provider_id, provider_revision, granted_utc, expires_utc, revoked_utc)
            VALUES ($request, $provider, $revision, $now, $expires, NULL)
            ON CONFLICT(request_id) DO UPDATE SET
                provider_id = excluded.provider_id, provider_revision = excluded.provider_revision,
                granted_utc = excluded.granted_utc, expires_utc = excluded.expires_utc,
                revoked_utc = NULL
            """, ("$request", request), ("$provider", provider), ("$revision", revision),
            ("$now", now), ("$expires", CatalogValues.Timestamp(clock.GetUtcNow() + DisclosureLifetime)));
        connection.Run(transaction, "DELETE FROM verification_disclosure_sources WHERE request_id = $request",
            ("$request", request));
        foreach (var (source, policyRevision) in policies)
        {
            connection.Run(transaction, """
                INSERT INTO verification_disclosure_sources (request_id, source_id, policy_revision)
                VALUES ($request, $source, $revision)
                """, ("$request", request), ("$source", source), ("$revision", policyRevision));
        }

        return true;
    }

    private static bool RemoteScopeValid(
        SqliteConnection connection, SqliteTransaction transaction, string request,
        string provider, int revision, string now)
    {
        using var grant = connection.CreateCommand("""
            SELECT provider_id, provider_revision, expires_utc, revoked_utc
            FROM verification_disclosure_grants WHERE request_id = $request
            """);
        grant.Transaction = transaction;
        grant.Parameters.AddWithValue("$request", request);
        using (var reader = grant.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != provider || reader.GetInt32(1) != revision ||
                string.CompareOrdinal(reader.GetString(2), now) <= 0 || !reader.IsDBNull(3))
            {
                return false;
            }
        }

        HashSet<string> required = IncludedSources(connection, transaction, request);
        if (required.Count == 0)
        {
            return false;
        }

        using var sources = connection.CreateCommand("""
            SELECT ds.source_id, ds.policy_revision, p.revision, p.classification, s.status
            FROM verification_disclosure_sources ds
            LEFT JOIN source_verification_policy p ON p.source_id = ds.source_id
            JOIN sources s ON s.source_id = ds.source_id
            WHERE ds.request_id = $request
            """);
        sources.Transaction = transaction;
        sources.Parameters.AddWithValue("$request", request);
        using var bindings = sources.ExecuteReader();
        int count = 0;
        while (bindings.Read())
        {
            if (!required.Contains(bindings.GetString(0)) || bindings.IsDBNull(2) ||
                bindings.GetInt32(1) != bindings.GetInt32(2) ||
                bindings.GetString(3) != "remote_verification" || bindings.GetString(4) != "active")
            {
                return false;
            }

            count++;
        }

        return count == required.Count;
    }

    private static void DenyDisclosure(SqliteConnection connection, SqliteTransaction transaction,
        string request, string now, string reason)
    {
        connection.Run(transaction, """
            UPDATE verification_invocations SET status = 'failed', finished_utc = $now, failure_code = $reason
            WHERE request_id = $request AND status = 'running'
            """, ("$request", request), ("$now", now), ("$reason", reason));
        connection.Run(transaction, """
            UPDATE verification_requests
            SET status = 'failed', completed_utc = $now, failure_code = $reason,
                lease_key = NULL, lease_until_utc = NULL, retry_after_utc = NULL
            WHERE request_id = $request AND status IN ('approved', 'running')
            """, ("$request", request), ("$now", now), ("$reason", reason));
        Audit(connection, transaction, request, null, null, "denial", "denied", reason, now);
    }

    private static void Audit(SqliteConnection connection, SqliteTransaction transaction,
        string? request, string? source, string? provider, string operation, string result,
        string reason, string now)
    {
        connection.Run(transaction, """
            INSERT INTO verification_disclosure_audit
                (event_id, request_id, source_id, provider_id, actor, operation, result, reason_code, occurred_utc)
            VALUES ($event, $request, $source, $provider, 'owner-local', $operation, $result, $reason, $now)
            """, ("$event", Guid.NewGuid().ToString("D")), ("$request", request),
            ("$source", source), ("$provider", provider), ("$operation", operation),
            ("$result", result), ("$reason", reason), ("$now", now));
        connection.Run(transaction, """
            DELETE FROM verification_disclosure_audit
            WHERE event_id NOT IN (
                SELECT event_id FROM verification_disclosure_audit
                ORDER BY occurred_utc DESC, event_id DESC LIMIT 10000)
            """);
    }
}
