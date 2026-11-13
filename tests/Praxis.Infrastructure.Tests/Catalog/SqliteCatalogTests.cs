using Microsoft.Data.Sqlite;
using Praxis.Domain.Originals;
using Praxis.Domain.Sources;
using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Catalog;

public sealed class SqliteCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-storage-test-" + Guid.NewGuid().ToString("N"));

    public SqliteCatalogTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void NewCatalogOpensInOwnerDirectoryWithVersionedConstraints()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        Assert.Equal(CatalogSchema.CurrentVersion, catalog.SchemaVersion);
        Assert.Equal(Path.Combine(root, "catalog.sqlite"), catalog.DatabasePath);
        using SqliteConnection reader = Connect(catalog.DatabasePath);
        Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(reader, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reader, "PRAGMA foreign_keys"));
        Assert.Equal(2L, Scalar(reader, "PRAGMA synchronous"));
        Assert.Equal("wal", Scalar(reader, "PRAGMA journal_mode"));
        Assert.Equal(14L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('original_objects', 'sources', 'items', 'item_versions', 'ingest_requests', 'processing_jobs', 'text_derivatives', 'search_index_keys', 'source_collections', 'source_collection_memberships', 'enrichment_runs', 'enrichment_proposals', 'enrichment_reviews', 'source_locator_moves')"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='capture_request_context'"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='semantic_vectors'"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='intake_incidents'"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='verification_contexts'"));
        Assert.Equal(4L, Scalar(reader, """
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'
            AND name IN ('source_verification_policy', 'verification_disclosure_grants',
                         'verification_disclosure_sources', 'verification_disclosure_audit')
            """));
        Assert.Equal("ok", Scalar(reader, "PRAGMA integrity_check"));
        Assert.Equal(0L, Scalar(reader, "SELECT COUNT(*) FROM sources"));
    }

    [Fact]
    public void TransactionRollbackAndReopenPreserveCommittedSource()
    {
        string path;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root))
        {
            path = catalog.DatabasePath;
            using SqliteConnection connection = Connect(path);
            InsertSource(connection, "committed", "note.md");
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO sources(source_id, source_kind, locator, created_utc)
                    VALUES ('rolled-back', 'file', 'other.md', '2026-11-08T00:00:00Z')
                    """;
                command.ExecuteNonQuery();
                transaction.Rollback();
            }
            Assert.Throws<IOException>(() => SqliteCatalog.Open(root));
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(root);
        using SqliteConnection connection2 = Connect(path);
        Assert.Equal(1L, Scalar(connection2, "SELECT COUNT(*) FROM sources"));
        Assert.Equal("committed", Scalar(connection2, "SELECT source_id FROM sources"));
    }

    [Fact]
    public void SharedContentKeepsIndependentSourceIdentityAndForeignKeys()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        using SqliteConnection connection = Connect(catalog.DatabasePath);
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Execute(connection, """
            INSERT INTO original_objects VALUES
              ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
               5, '2026-11-08T00:00:00Z')
            """);
        InsertSource(connection, "first", "a.md");
        InsertSource(connection, "second", "b.md");
        Execute(connection, "INSERT INTO items VALUES ('item-a', 'first'), ('item-b', 'second')");
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO item_versions VALUES
                  ('item-a', 1, $digest, '2026-11-08T00:00:00Z'),
                  ('item-b', 1, $digest, '2026-11-08T00:00:00Z')
                """;
            command.Parameters.AddWithValue("$digest", digest);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM original_objects"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM item_versions"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO items VALUES ('item-c', 'missing-source')"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code) VALUES ('key', 'missing-source', 'queued', 0, NULL)"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM ingest_requests"));
    }

    [Fact]
    public void CorruptAndUnknownCatalogsAreRejectedWithoutOverwriting()
    {
        using (SqliteCatalog catalog = SqliteCatalog.Open(root)) { }
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            Execute(connection, $"PRAGMA user_version = {CatalogSchema.CurrentVersion + 1}");
        }

        Assert.Throws<NotSupportedException>(() => SqliteCatalog.Open(root));
        using (SqliteConnection connection = Connect(path))
        {
            Assert.Equal((long)CatalogSchema.CurrentVersion + 1, Scalar(connection, "PRAGMA user_version"));
        }

        File.Delete(path);
        File.WriteAllBytes(path, [5, 7, 9]);
        Assert.Throws<SqliteException>(() => SqliteCatalog.Open(root));
        Assert.Equal([5, 7, 9], File.ReadAllBytes(path));
    }

    [Fact]
    public void VersionSixteenUpgradesIncidentJournalWithoutChangingSourceData()
    {
        CreateLegacyCatalog(16);
        string databasePath = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(databasePath))
        {
            InsertSource(connection, "preserved-source", "original.md");
            Assert.Null(Scalar(connection, "SELECT name FROM sqlite_master WHERE name = 'intake_incidents'"));
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(root);
        Assert.Equal(CatalogSchema.CurrentVersion, upgraded.SchemaVersion);
        using SqliteConnection verified = Connect(upgraded.DatabasePath);
        Assert.Equal(1L, Scalar(verified, "SELECT COUNT(*) FROM sources"));
        Assert.Equal("preserved-source", Scalar(verified, "SELECT source_id FROM sources"));
        Assert.Equal(0L, Scalar(verified, "SELECT COUNT(*) FROM intake_incidents"));
        Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(verified, "PRAGMA user_version"));
    }

    [Fact]
    public void VersionSeventeenPreservesVerifierApprovalAndAttemptHistory()
    {
        CreateLegacyCatalog(17);
        Guid requestId = Guid.NewGuid();
        using (SqliteConnection connection = Connect(Path.Combine(root, "catalog.sqlite")))
        {
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO verification_providers
                    (provider_id, executable, model, cost_weight, daily_limit, enabled, updated_utc)
                VALUES ('codex', '/usr/local/bin/codex', 'gpt-5.2', 10, 20, 1, '2026-11-08T00:00:00Z');
                INSERT INTO verification_requests
                    (request_id, reason, status, provider_id, payload, created_utc, decided_utc,
                     started_utc, completed_utc, model_name, model_version, input_tokens, output_tokens, duration_ms)
                VALUES ($request, 'owner_request', 'completed', 'codex', '{"items":[]}',
                        '2026-11-08T00:00:00Z', '2026-11-08T00:01:00Z', '2026-11-08T00:02:00Z',
                        '2026-11-08T00:03:00Z', 'gpt-5.2', 'old-model-digest', 21, 7, 120);
                """;
            seed.Parameters.AddWithValue("$request", requestId.ToString("D"));
            seed.ExecuteNonQuery();
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(root);
        var restored = upgraded.Verification.Get(requestId)!;
        Assert.Equal("completed", restored.Status);
        Assert.Equal("codex", restored.ProviderId);
        Assert.Equal("gpt-5.2", restored.ModelName);
        Assert.Equal(21, restored.InputTokens);
        using SqliteConnection verified = Connect(upgraded.DatabasePath);
        Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(verified, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(verified, "SELECT config_revision FROM verification_providers WHERE provider_id='codex'"));
        Assert.Equal(1L, Scalar(verified, "SELECT approved_provider_revision FROM verification_requests"));
        Assert.Equal(requestId.ToString("D"), Scalar(verified, "SELECT request_id FROM verification_invocations"));
        Assert.Equal("legacy", Scalar(verified, "SELECT status FROM verification_invocations"));
        Assert.Equal(21L, Scalar(verified, "SELECT input_tokens FROM verification_invocations"));
        Assert.Equal("old-model-digest", Scalar(verified, "SELECT model_version FROM verification_invocations"));
        Assert.Null(Scalar(verified, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public void VersionEighteenUnboundEntityReviewIsRetiredWithoutRetainingAnActiveLease()
    {
        CreateLegacyCatalog(18);
        Guid entity = Guid.NewGuid();
        Guid request = Guid.NewGuid();
        Guid attempt = Guid.NewGuid();
        using (SqliteConnection old = Connect(Path.Combine(root, "catalog.sqlite")))
        {
            using SqliteCommand seed = old.CreateCommand();
            seed.CommandText = """
                INSERT INTO knowledge_entities
                    (entity_id, display_name, entity_type, type_origin, status, created_utc, updated_utc)
                VALUES ($entity, 'Old entity', 'concept', 'inferred', 'active',
                        '2026-11-12T00:00:00Z', '2026-11-12T00:00:00Z');
                INSERT INTO verification_providers
                    (provider_id, executable, model, cost_weight, daily_limit, enabled, updated_utc)
                VALUES ('codex', '/usr/local/bin/codex', NULL, 10, 20, 1, '2026-11-12T00:00:00Z');
                INSERT INTO verification_requests
                    (request_id, reason, status, provider_id, payload, created_utc,
                     approved_provider_revision, lease_key, lease_until_utc)
                VALUES ($request, 'owner_request', 'running', 'codex', '{"items":[{"ordinal":1}]}',
                        '2026-11-12T00:00:00Z', 1, $attempt, '2026-11-14T00:00:00Z');
                INSERT INTO verification_items (request_id, ordinal, entity_id, included)
                VALUES ($request, 1, $entity, 1);
                INSERT INTO verification_invocations
                    (attempt_id, request_id, provider_id, provider_revision, started_utc, status)
                VALUES ($attempt, $request, 'codex', 1, '2026-11-12T00:00:00Z', 'running');
                """;
            seed.Parameters.AddWithValue("$entity", entity.ToString("D"));
            seed.Parameters.AddWithValue("$request", request.ToString("D"));
            seed.Parameters.AddWithValue("$attempt", attempt.ToString("D"));
            seed.ExecuteNonQuery();
        }

        using SqliteCatalog migrated = SqliteCatalog.Open(root);
        using SqliteConnection verified = Connect(migrated.DatabasePath);
        Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(verified, "PRAGMA user_version"));
        Assert.Equal("failed", Scalar(verified, "SELECT status FROM verification_requests"));
        Assert.Equal("source_changed", Scalar(verified, "SELECT failure_code FROM verification_requests"));
        Assert.Equal(1L, Scalar(verified, "SELECT lease_key IS NULL AND lease_until_utc IS NULL FROM verification_requests"));
        Assert.Equal("failed", Scalar(verified, "SELECT status FROM verification_invocations"));
        Assert.Equal("source_changed", Scalar(verified, "SELECT failure_code FROM verification_invocations"));
        Assert.Equal(0L, Scalar(verified, "SELECT COUNT(*) FROM verification_contexts"));
        Assert.Null(Scalar(verified, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public void VersionNineteenExternalApprovalsAreRetiredWithoutCreatingDisclosureGrants()
    {
        CreateLegacyCatalog(19);
        Guid external = Guid.NewGuid();
        Guid local = Guid.NewGuid();
        using (SqliteConnection old = Connect(Path.Combine(root, "catalog.sqlite")))
        {
            using SqliteCommand seed = old.CreateCommand();
            seed.CommandText = """
                INSERT INTO verification_providers
                    (provider_id, executable, endpoint, model, cost_weight, daily_limit, enabled,
                     updated_utc, config_revision)
                VALUES ('codex', '/usr/bin/codex', NULL, NULL, 10, 20, 1, '2026-11-12T00:00:00Z', 1),
                       ('ollama', NULL, 'http://127.0.0.1:11434', 'qwen:9b', 0, 20, 1,
                        '2026-11-12T00:00:00Z', 1);
                INSERT INTO verification_requests
                    (request_id, reason, status, provider_id, payload, created_utc, approved_provider_revision)
                VALUES ($external, 'owner_request', 'approved', 'codex', '{}', '2026-11-12T00:00:00Z', 1),
                       ($local, 'owner_request', 'approved', 'ollama', '{}', '2026-11-12T00:00:00Z', 1);
                """;
            seed.Parameters.AddWithValue("$external", external.ToString("D"));
            seed.Parameters.AddWithValue("$local", local.ToString("D"));
            seed.ExecuteNonQuery();
        }

        using SqliteCatalog migrated = SqliteCatalog.Open(root);
        Assert.Equal("failed", migrated.Verification.Get(external)!.Status);
        Assert.Equal("disclosure_denied", migrated.Verification.Get(external)!.FailureCode);
        Assert.Equal("approved", migrated.Verification.Get(local)!.Status);
        using SqliteConnection verified = Connect(migrated.DatabasePath);
        Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(verified, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(verified, "SELECT COUNT(*) FROM verification_disclosure_grants"));
        Assert.Equal(0L, Scalar(verified, "SELECT COUNT(*) FROM source_verification_policy"));
        Assert.Null(Scalar(verified, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public void LegacyVersionOneCatalogUpgradesAndPreservesExistingHistory()
    {
        CreateLegacyCatalog(1);
        using (SqliteConnection connection = Connect(Path.Combine(root, "catalog.sqlite")))
        {
            InsertSource(connection, "old-source", "source.md");
            Execute(connection, """
                INSERT INTO original_objects VALUES
                  ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                   5, '2026-11-08T00:00:00Z');
                INSERT INTO items VALUES ('old-item', 'old-source');
                INSERT INTO item_versions VALUES
                  ('old-item', 1,
                   'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                   '2026-11-08T00:00:00Z');
                INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code)
                    VALUES ('old-queued', 'old-source', 'queued', 0, NULL);
                """);
        }

        using (SqliteCatalog upgraded = SqliteCatalog.Open(root))
        {
            Assert.Equal(CatalogSchema.CurrentVersion, upgraded.SchemaVersion);
            using SqliteConnection connection = Connect(upgraded.DatabasePath);
            Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(connection, "PRAGMA user_version"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sources"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM item_versions"));
            Assert.Equal(1L, Scalar(connection,
                "SELECT COUNT(*) FROM ingest_requests WHERE revision IS NULL AND state='queued'"));
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(root);
        using SqliteConnection verified = Connect(reopened.DatabasePath);
        Assert.Equal((long)CatalogSchema.CurrentVersion, Scalar(verified, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(verified, "SELECT COUNT(*) FROM original_objects"));
        Assert.Equal(1L, Scalar(verified, "SELECT COUNT(*) FROM processing_jobs"));
        Assert.Equal(0L, Scalar(verified, "SELECT COUNT(*) FROM text_derivatives"));
    }

    [Fact]
    public async Task PriorSchemaReceiptsUpgradeWithoutManufacturingAuthorization()
    {
        CreateLegacyCatalog(14);
        Guid sourceId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        using MemoryStream bytes = new("Legacy original"u8.ToArray());
        OriginalObject original = await new OriginalFileStore(root).PublishAsync(
            bytes, 200, CancellationToken.None);

        using (SqliteConnection connection = Connect(Path.Combine(root, "catalog.sqlite")))
        {
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO original_objects (digest, byte_length, recorded_utc)
                VALUES ($digest, $length, '2026-11-08T00:00:00Z');
                INSERT INTO sources (source_id, source_kind, locator, created_utc)
                VALUES ($source, 'file', $locator, '2026-11-08T00:00:00Z');
                INSERT INTO items (item_id, source_id) VALUES ($item, $source);
                INSERT INTO item_versions (item_id, revision, digest, observed_utc)
                VALUES ($item, 1, $digest, '2026-11-08T00:00:00Z');
                INSERT INTO ingest_requests (request_key, source_id, state, attempt, revision)
                VALUES ('legacy-key', $source, 'committed', 1, 1);
                """;
            seed.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            seed.Parameters.AddWithValue("$item", itemId.ToString("D"));
            seed.Parameters.AddWithValue("$digest", original.Digest);
            seed.Parameters.AddWithValue("$length", original.Length);
            seed.Parameters.AddWithValue("$locator", Path.Combine(TestPaths.Temp, "legacy-file.md"));
            seed.ExecuteNonQuery();
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(root);
        Assert.Equal(CatalogSchema.CurrentVersion, upgraded.SchemaVersion);
        Assert.Equal("file", upgraded.Sources.ReadProvenance(sourceId)!.SourceKind);
        Assert.Null(upgraded.Sources.ReadProvenance(sourceId)!.CaptureRoute);
        CaptureReceipt receipt = Assert.Single(upgraded.Sources.ListCaptureReceipts(sourceId));
        Assert.Equal("legacy-key", receipt.RequestKey);
        Assert.Null(receipt.Route);
        Assert.Null(receipt.AuthorityBasis);
        Assert.Null(receipt.RecordedUtc);
        await upgraded.Originals.VerifyAsync(itemId);
    }

    [Fact]
    public void UnversionedForeignTablesAreNotAdopted()
    {
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            Execute(connection, "CREATE TABLE unrelated (id INTEGER)");
        }
        Assert.Throws<InvalidDataException>(() => SqliteCatalog.Open(root));
        using SqliteConnection reopened = Connect(path);
        Assert.Equal(0L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'unrelated'"));
    }

    [Fact]
    public void FalselyVersionedCatalogWithIncompatibleColumnsIsRejected()
    {
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            Execute(connection, """
                CREATE TABLE original_objects (placeholder INTEGER) STRICT;
                CREATE TABLE sources (placeholder INTEGER) STRICT;
                CREATE TABLE items (placeholder INTEGER) STRICT;
                CREATE TABLE item_versions (placeholder INTEGER) STRICT;
                CREATE TABLE ingest_requests (placeholder INTEGER) STRICT;
                PRAGMA user_version = 1;
                """);
        }

        Assert.Throws<InvalidDataException>(() => SqliteCatalog.Open(root));
        using SqliteConnection reopened = Connect(path);
        Assert.Equal(1L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sources'"));
    }

    [Fact]
    public void MissingRelativeAndSharedDirectoriesAreRejectedWithoutCreatingCatalog()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            SqliteCatalog.Open(Path.Combine(root, "not-created")));
        Assert.Throws<ArgumentException>(() => SqliteCatalog.Open("relative/catalog"));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.OtherExecute);
            Assert.Throws<UnauthorizedAccessException>(() => SqliteCatalog.Open(root));
            Assert.False(File.Exists(Path.Combine(root, "catalog.sqlite")));
        }
    }

    [Fact]
    public void SymlinkedAndUnknownDataRootEntriesAreRejected()
    {
        string unrelated = Path.Combine(root, "my-notes.md");
        File.WriteAllText(unrelated, "Do not modify user files");
        Assert.Throws<InvalidDataException>(() => SqliteCatalog.Open(root));
        Assert.Equal("Do not modify user files", File.ReadAllText(unrelated));
        File.Delete(unrelated);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string other = Path.Combine(TestPaths.Temp, "praxis-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            string link = Path.Combine(root, "objects");
            Directory.CreateSymbolicLink(link, other);
            Assert.Throws<InvalidDataException>(() => SqliteCatalog.Open(root));
            Directory.Delete(link);
        }
        finally
        {
            Directory.Delete(other);
        }
    }

    private void CreateLegacyCatalog(int version)
    {
        if (version is < 1 or > 19)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        using SqliteConnection connection = Connect(Path.Combine(root, "catalog.sqlite"));
        Execute(connection, "PRAGMA foreign_keys = OFF");
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            for (int target = 1; target <= version; target++)
            {
                using Stream resource = typeof(SqliteCatalog).Assembly
                    .GetManifestResourceStream($"Praxis.Infrastructure.Catalog.V{target}.sql")!;
                using StreamReader source = new(resource);
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = source.ReadToEnd();
                migration.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Execute(connection, "PRAGMA foreign_keys = ON");
        Assert.Equal((long)version, Scalar(connection, "PRAGMA user_version"));
        Assert.Null(Scalar(connection, "PRAGMA foreign_key_check"));
    }

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertSource(SqliteConnection connection, string id, string locator)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sources(source_id, source_kind, locator, created_utc)
            VALUES ($id, 'file', $locator, '2026-11-08T00:00:00Z')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$locator", locator);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
