using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

internal static class CatalogSchema
{
    internal const int CurrentVersion = 13;
    private static readonly (int Since, string Name, string Columns)[] Tables =
    [
        (1, "original_objects", "digest, byte_length, recorded_utc"),
        (1, "sources", "source_id, source_kind, locator, created_utc, status"),
        (1, "items", "item_id, source_id"),
        (1, "item_versions", "item_id, revision, digest, observed_utc"),
        (1, "ingest_requests", "request_key, source_id, state, attempt, failure_code"),
        (3, "processing_jobs", "item_id, revision, processor_key, state, attempt, total_attempts, lease_key, lease_until_utc, failure_code"),
        (4, "text_derivatives", "item_id, revision, processor_key, source_digest, outcome, content, reason_code, created_utc"),
        (5, "search_index_keys", "rowid, item_id, revision, source_id"),
        (6, "source_collections", "collection_id, name, name_key, created_utc, archived_utc"),
        (6, "source_collection_memberships", "collection_id, source_id, created_utc"),
        (7, "enrichment_runs", "item_id, revision, processor_key, source_digest, outcome, created_utc"),
        (7, "enrichment_proposals", "proposal_id, item_id, revision, processor_key, source_digest, kind, value, evidence, evidence_offset, status, correction, created_utc, reviewed_utc"),
        (7, "enrichment_reviews", "review_id, proposal_id, decision, correction, reviewed_utc"),
        (8, "source_locator_moves", "move_id, source_id, old_locator, new_locator, digest, recorded_utc"),
        (10, "processing_jobs", "priority"),
        (10, "text_segments", "item_id, revision, processor_key, ordinal, start_offset, length, heading_path"),
        (11, "model_profiles", "role, provider, endpoint, model, context_tokens, enabled, updated_utc"),
        (12, "enrichment_proposals", "entity_type, evidence_length"),
        (13, "enrichment_proposals", "relation_subject, relation_object, flags"),
        (13, "knowledge_entities", "entity_id, display_name, entity_type, type_origin, status, merged_into, created_utc, updated_utc"),
        (13, "entity_aliases", "alias_key, entity_id, display, origin, created_utc"),
        (13, "entity_mentions", "proposal_id, entity_id, created_utc"),
        (13, "knowledge_relations", "relation_id, subject_id, predicate, object_id, origin, proposal_id, created_utc"),
        (13, "entity_decisions", "decision_id, entity_id, action, detail, decided_utc"),
        (13, "enrichment_runs", "segments_reused"),
        (12, "enrichment_runs", "model_provider, model_name, model_digest, input_tokens, output_tokens, duration_ms, segments_examined, segments_total, rejected_count")
    ];

    internal static void Prepare(SqliteConnection database)
    {
        Execute(database, "PRAGMA foreign_keys = ON");
        Execute(database, "PRAGMA busy_timeout = 5000");
        VerifyIntegrity(database);

        var version = ReadVersion(database);
        if (version is < 0 or > CurrentVersion)
        {
            throw new NotSupportedException("The catalog schema version is not supported.");
        }

        if (version == 0 && Convert.ToInt64(Scalar(database, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            """)) != 0)
        {
            throw new InvalidDataException("An unversioned database already contains user tables.");
        }

        if (version is >= 1 and < CurrentVersion)
        {
            VerifySchema(database, version);
        }

        if (version < CurrentVersion)
        {
            Migrate(database, version);
        }

        VerifySchema(database, CurrentVersion);
        VerifySearchSchema(database);
        VerifyEnrichmentView(database);
        if (!string.Equals(Scalar(database, "PRAGMA journal_mode = WAL") as string,
            "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The data filesystem cannot use SQLite WAL.");
        }

        Execute(database, "PRAGMA synchronous = FULL");
        if (Convert.ToInt32(Scalar(database, "PRAGMA synchronous")) != 2 ||
            Convert.ToInt32(Scalar(database, "PRAGMA foreign_keys")) != 1)
        {
            throw new InvalidDataException("The SQLite durability or foreign key configuration is invalid.");
        }
    }

    /// <summary>
    /// Applies pending migrations in one transaction using SQLite's documented table-rebuild
    /// procedure: foreign keys are disabled for the batch (the pragma cannot change inside a
    /// transaction), every reference is verified with foreign_key_check before commit, and
    /// enforcement is restored whether or not the upgrade succeeds.
    /// </summary>
    private static void Migrate(SqliteConnection database, int version)
    {
        Execute(database, "PRAGMA foreign_keys = OFF");
        try
        {
            using var transaction = database.BeginTransaction();
            for (var target = version + 1; target <= CurrentVersion; target++)
            {
                var resource = target == 1 ? "LocalCatalog.sql" : $"LocalCatalog.V{target}.sql";
                using var migration = database.CreateCommand(ReadSchema(resource));
                migration.Transaction = transaction;
                migration.ExecuteNonQuery();
            }

            using var versionCheck = database.CreateCommand("PRAGMA user_version");
            versionCheck.Transaction = transaction;
            if (Convert.ToInt32(versionCheck.ExecuteScalar()) != CurrentVersion)
            {
                throw new InvalidDataException("The catalog migration did not reach the expected version.");
            }

            using var references = database.CreateCommand("PRAGMA foreign_key_check");
            references.Transaction = transaction;
            using (var violation = references.ExecuteReader())
            {
                if (violation.Read())
                {
                    throw new InvalidDataException("The catalog migration left invalid foreign key references.");
                }
            }

            transaction.Commit();
        }
        finally
        {
            Execute(database, "PRAGMA foreign_keys = ON");
        }
    }

    internal static int ReadVersion(SqliteConnection database) =>
        Convert.ToInt32(Scalar(database, "PRAGMA user_version"));

    internal static void VerifyIntegrity(SqliteConnection database)
    {
        if (!string.Equals(Scalar(database, "PRAGMA integrity_check") as string,
                "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The catalog failed SQLite integrity verification.");
        }

        using var foreignKeys = database.CreateCommand("PRAGMA foreign_key_check");
        using var badReference = foreignKeys.ExecuteReader();
        if (badReference.Read())
        {
            throw new InvalidDataException("The catalog contains invalid foreign key references.");
        }
    }

    internal static void VerifySchema(SqliteConnection database, int version)
    {
        foreach (var (since, name, columns) in Tables)
        {
            if (version < since)
            {
                continue;
            }

            using var command = database.CreateCommand(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name");
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string definition ||
                !definition.TrimEnd().EndsWith("STRICT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The catalog schema is incompatible.");
            }

            var requiredColumns = version >= 2 && name == "ingest_requests"
                ? $"{columns}, revision"
                : columns;
            using var projection = database.CreateCommand($"SELECT {requiredColumns} FROM {name} LIMIT 0");
            try
            {
                using var reader = projection.ExecuteReader();
            }
            catch (SqliteException exception)
            {
                throw new InvalidDataException("The catalog schema is incomplete.", exception);
            }
        }
    }

    private static void VerifyEnrichmentView(SqliteConnection database)
    {
        using var definition = database.CreateCommand("""
            SELECT sql FROM sqlite_master WHERE type = 'view'
              AND name = 'eligible_enrichment_proposals'
            """);
        if (definition.ExecuteScalar() is not string)
        {
            throw new InvalidDataException("The enrichment eligibility view is missing.");
        }

        using var projection = database.CreateCommand(
            "SELECT source_id, proposal_id FROM eligible_enrichment_proposals LIMIT 0");
        try
        {
            using var reader = projection.ExecuteReader();
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The enrichment eligibility view is incompatible.", exception);
        }
    }

    private static void VerifySearchSchema(SqliteConnection database)
    {
        foreach (var (kind, name) in new (string Kind, string Name)[]
        {
            ("table", "text_search"),
            ("view", "eligible_text_search"),
            ("trigger", "text_search_new_version"),
            ("trigger", "text_search_source_revoked"),
            ("trigger", "text_search_job_completed")
        })
        {
            using var command = database.CreateCommand("""
                SELECT sql FROM sqlite_master WHERE type = $type AND name = $name
                """);
            command.Parameters.AddWithValue("$type", kind);
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string definition ||
                (name == "text_search" &&
                 !definition.Contains("USING fts5", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The local search schema is incompatible.");
            }
        }

        using var projection = database.CreateCommand(
            "SELECT locator, content FROM text_search LIMIT 0");
        try
        {
            using var reader = projection.ExecuteReader();
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The local search index is unavailable.", exception);
        }
    }

    private static string ReadSchema(string fileName)
    {
        using var stream = typeof(CatalogSchema).Assembly
            .GetManifestResourceStream($"Praxis.Storage.{fileName}")
            ?? throw new InvalidOperationException("The catalog migration resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        command.ExecuteNonQuery();
    }
}
