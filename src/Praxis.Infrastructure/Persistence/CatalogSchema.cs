using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence;

internal static class CatalogSchema
{
    internal const int CurrentVersion = 34;
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
        (14, "verification_providers", "provider_id, executable, model, cost_weight, daily_limit, enabled, updated_utc"),
        (14, "verification_requests", "request_id, reason, status, provider_id, payload, created_utc, decided_utc, started_utc, completed_utc, retry_after_utc, failure_code, model_name, model_version, input_tokens, output_tokens, duration_ms, lease_key, lease_until_utc"),
        (14, "verification_items", "request_id, ordinal, proposal_id, entity_id, included, verdict, corrected_type, note"),
        (18, "verification_providers", "endpoint, config_revision"),
        (18, "verification_requests", "approved_provider_revision"),
        (18, "verification_invocations", "attempt_id, request_id, provider_id, provider_revision, started_utc, status"),
        (19, "verification_contexts", "request_id, ordinal, proposal_id, source_id, revision, source_digest"),
        (20, "source_verification_policy", "source_id, classification, revision, changed_utc"),
        (20, "verification_disclosure_grants", "request_id, provider_id, provider_revision, granted_utc, expires_utc, revoked_utc"),
        (20, "verification_disclosure_sources", "request_id, source_id, policy_revision"),
        (20, "verification_disclosure_audit", "event_id, request_id, source_id, provider_id, actor, operation, result, reason_code, occurred_utc"),
        (15, "capture_request_context", "request_key, route, authority_basis, scope, recorded_utc"),
        (16, "semantic_vectors", "item_id, revision, ordinal, model_digest, source_digest, dimensions, vector, segment_text, recorded_utc"),
        (17, "intake_incidents", "incident_id, root_path, state, started_utc, resolved_utc"),
        (22, "agent_backends", "backend_id, kind, endpoint, model, key_account, executable, enabled, updated_utc"),
        (22, "agent_conversations", "conversation_id, backend_id, title, created_utc, updated_utc"),
        (31, "agent_conversations", "model, effort"),
        (32, "reconsolidation_queue", "entity_id, reason, previous_type, enqueued_utc"),
        (32, "knowledge_revisions", "revision_id, entity_id, field, before_value, after_value, trigger, basis, state, created_utc, decided_utc"),
        (33, "knowledge_revisions", "relation_id"),
        (33, "graph_revision", "singleton, revision"),
        (34, "agent_skill_revisions", "revision_id, name, description, markdown, digest, base_revision_id, conversation_id, origin, rationale, state, created_utc, decided_utc"),
        (34, "agent_skill_installs", "name, revision_id"),
        (34, "agent_skill_disabled", "name"),
        (22, "agent_messages", "conversation_id, sequence, role, content, tool_calls, tool_call_id, created_utc"),
        (22, "agent_tool_calls", "record_id, conversation_id, call_id, tool, tier, arguments, outcome, result_characters, citations, withheld, started_utc, duration_ms"),
        (22, "agent_proposals", "proposal_id, conversation_id, kind, payload, rationale, status, created_utc, resolved_utc"),
        (22, "agent_memories", "memory_id, conversation_id, text, status, created_utc, updated_utc"),
        (22, "agent_artifacts", "artifact_id, title, source_id, revision, path, conversation_id, updated_utc"),
        (21, "financial_documents", "item_id, revision, processor_key, source_digest, outcome, format, parser, reason_code, issues, tier, model_provider, model_name, model_digest, created_utc"),
        (21, "financial_annotations", "item_id, revision, records_processor, ordinal, merchant, category, tier, provider_id, model_name, created_utc"),
        (21, "model_tiers", "tier, provider_id, updated_utc"),
        (21, "disclosure_settings", "singleton, mode, tool_permission, hold_hours, updated_utc"),
        (21, "disclosure_rules", "rule_id, category, provider_id, purpose, allow, expires_utc, created_utc"),
        (21, "disclosure_fallbacks", "category, chain, updated_utc"),
        (21, "policy_changes", "change_id, payload, requested_by, rationale, status, requested_utc, resolved_utc"),
        (21, "source_categories", "source_id, category, origin, local_only, updated_utc"),
        (21, "disclosure_holds", "hold_id, item_id, revision, processor_key, category, provider_id, purpose, status, deadline_utc, created_utc, decided_utc"),
        (21, "model_invocations", "invocation_id, tier, provider_id, remote, purpose, category, decision_reason, item_id, revision, processor_key, model_name, input_tokens, output_tokens, outcome, started_utc"),
        (21, "financial_accounts", "item_id, revision, processor_key, account_key, kind, mask, institution, currency, display_name"),
        (21, "financial_statements", "item_id, revision, processor_key, ordinal, account_key, period_start, period_end, opening_minor, closing_minor, currency, verification, verification_note"),
        (21, "financial_transactions", "item_id, revision, processor_key, ordinal, account_key, statement_ordinal, posted_on, amount_minor, currency, description, status, provider_type, provider_id, check_number, provider_category, balance_minor, locator, origin"),
        (12, "enrichment_runs", "model_provider, model_name, model_digest, input_tokens, output_tokens, duration_ms, segments_examined, segments_total, rejected_count")
    ];

    internal static void Prepare(SqliteConnection database)
    {
        Execute(database, "PRAGMA foreign_keys = ON");
        Execute(database, "PRAGMA busy_timeout = 5000");
        VerifyIntegrity(database);

        int version = ReadVersion(database);
        switch (version)
        {
            case < 0 or > CurrentVersion:
                throw new NotSupportedException("The catalog schema version is not supported.");
            case 0 when Convert.ToInt64(Scalar(database, """
                                                         SELECT COUNT(*) FROM sqlite_master
                                                         WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                                                         """)) != 0:
                throw new InvalidDataException("An unversioned database already contains user tables.");
            case >= 1 and < CurrentVersion:
                VerifySchema(database, version);
                break;
        }

        if (version < CurrentVersion)
        {
            Migrate(database, version);
        }

        VerifySchema(database, CurrentVersion);
        VerifySearchSchema(database);
        VerifyEnrichmentView(database);
        VerifySemanticSchema(database);
        VerifyIntakeSchema(database);
        if (!string.Equals(Scalar(database, "PRAGMA journal_mode = WAL") as string, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The data filesystem cannot use SQLite WAL.");
        }

        Execute(database, "PRAGMA synchronous = FULL");
        if (Convert.ToInt32(Scalar(database, "PRAGMA synchronous")) != 2 || Convert.ToInt32(Scalar(database, "PRAGMA foreign_keys")) != 1)
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
            using SqliteTransaction transaction = database.BeginTransaction();
            for (int target = version + 1; target <= CurrentVersion; target++)
            {
                string resource = $"Catalog.V{target}.sql";
                using SqliteCommand migration = database.CreateCommand(ReadSchema(resource));
                migration.Transaction = transaction;
                migration.ExecuteNonQuery();
            }

            using SqliteCommand versionCheck = database.CreateCommand("PRAGMA user_version");
            versionCheck.Transaction = transaction;
            if (Convert.ToInt32(versionCheck.ExecuteScalar()) != CurrentVersion)
            {
                throw new InvalidDataException("The catalog migration did not reach the expected version.");
            }

            using SqliteCommand references = database.CreateCommand("PRAGMA foreign_key_check");
            references.Transaction = transaction;
            using (SqliteDataReader violation = references.ExecuteReader())
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
        if (!string.Equals(Scalar(database, "PRAGMA integrity_check") as string, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The catalog failed SQLite integrity verification.");
        }

        using SqliteCommand foreignKeys = database.CreateCommand("PRAGMA foreign_key_check");
        using SqliteDataReader badReference = foreignKeys.ExecuteReader();
        if (badReference.Read())
        {
            throw new InvalidDataException("The catalog contains invalid foreign key references.");
        }
    }

    internal static void VerifySchema(SqliteConnection database, int version)
    {
        foreach ((int since, string name, string columns) in Tables)
        {
            if (version < since)
            {
                continue;
            }

            using SqliteCommand command = database.CreateCommand("SELECT sql FROM sqlite_master WHERE type='table' AND name=$name");
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string definition || !definition.TrimEnd().EndsWith("STRICT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The catalog schema is incompatible.");
            }

            string requiredColumns = version >= 2 && name == "ingest_requests" ? $"{columns}, revision" : columns;
            using SqliteCommand projection = database.CreateCommand($"SELECT {requiredColumns} FROM {name} LIMIT 0");
            try
            {
                using SqliteDataReader reader = projection.ExecuteReader();
            }
            catch (SqliteException exception)
            {
                throw new InvalidDataException("The catalog schema is incomplete.", exception);
            }
        }
    }

    private static void VerifyEnrichmentView(SqliteConnection database)
    {
        using SqliteCommand definition = database.CreateCommand("""
                                                                SELECT sql FROM sqlite_master WHERE type = 'view'
                                                                  AND name = 'eligible_enrichment_proposals'
                                                                """);
        if (definition.ExecuteScalar() is not string)
        {
            throw new InvalidDataException("The enrichment eligibility view is missing.");
        }

        using SqliteCommand projection = database.CreateCommand("SELECT source_id, proposal_id FROM eligible_enrichment_proposals LIMIT 0");
        try
        {
            using SqliteDataReader reader = projection.ExecuteReader();
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The enrichment eligibility view is incompatible.", exception);
        }
    }

    private static void VerifySearchSchema(SqliteConnection database)
    {
        foreach ((string kind, string name) in new (string Kind, string Name)[]
        {
            ("table", "text_search"),
            ("view", "eligible_text_search"),
            ("trigger", "text_search_new_version"),
            ("trigger", "text_search_source_revoked"),
            ("trigger", "text_search_job_completed")
        })
        {
            using SqliteCommand command = database.CreateCommand("""
                                                                 SELECT sql FROM sqlite_master WHERE type = $type AND name = $name
                                                                 """);
            command.Parameters.AddWithValue("$type", kind);
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string definition || (name == "text_search" && !definition.Contains("USING fts5", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The local search schema is incompatible.");
            }
        }

        using SqliteCommand projection = database.CreateCommand("SELECT locator, content FROM text_search LIMIT 0");
        try
        {
            using SqliteDataReader reader = projection.ExecuteReader();
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The local search index is unavailable.", exception);
        }
    }

    private static void VerifySemanticSchema(SqliteConnection database)
    {
        foreach ((string kind, string name) in new (string, string)[]
        {
            ("view", "eligible_semantic_segments"),
            ("trigger", "semantic_vectors_new_version"),
            ("trigger", "semantic_vectors_source_revoked")
        })
        {
            using SqliteCommand command = database.CreateCommand("SELECT sql FROM sqlite_master WHERE type=$kind AND name=$name");
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string)
            {
                throw new InvalidDataException("The semantic search schema is incomplete.");
            }
        }
    }

    private static void VerifyIntakeSchema(SqliteConnection database)
    {
        foreach ((string kind, string name) in new (string, string)[]
        {
            ("view", "eligible_financial_documents"),
            ("trigger", "financial_records_source_revoked"),
            ("table", "intake_documents"),
            ("table", "intake_records"),
            ("table", "intake_layouts"),
            ("trigger", "intake_source_revoked"),
            ("view", "confirmed_entity_mentions"),
            ("view", "technical_entity_types"),
            ("table", "intake_cascades"),
            ("table", "runtime_settings"),
            ("table", "text_timings"),
            ("table", "link_snapshots"),
            ("table", "link_settings"),
            ("table", "intake_projects")
        })
        {
            using SqliteCommand command = database.CreateCommand("SELECT sql FROM sqlite_master WHERE type=$kind AND name=$name");
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string)
            {
                throw new InvalidDataException("The intake and ledger schema is incomplete.");
            }
        }
    }

    private static string ReadSchema(string fileName)
    {
        using Stream stream = typeof(CatalogSchema).Assembly.GetManifestResourceStream($"Praxis.Infrastructure.{fileName}")
                              ?? throw new InvalidOperationException("The catalog migration resource is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand(sql);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand(sql);
        command.ExecuteNonQuery();
    }
}
