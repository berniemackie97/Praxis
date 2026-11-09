using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalTextProcessorTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-text-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public LocalTextProcessorTests()
    {
        root = Path.Combine(workspace, "store");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace, PrivateDirectory);
            File.SetUnixFileMode(root, PrivateDirectory);
        }
    }

    [Fact]
    public async Task Utf8TextAndMarkdownPersistWithVersionedDigestAfterRestart()
    {
        CapturedOriginal text;
        CapturedOriginal markdown;
        var markdownBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("# Topic\nReady ✅")).ToArray();
        using (var catalog = LocalCatalog.Open(root))
        {
            text = await Capture(catalog, "plain.txt", Encoding.UTF8.GetBytes("hello\nworld"), "text");
            markdown = await Capture(catalog, "note.md", markdownBytes, "markdown");
            var processor = new LocalTextProcessor(catalog);
            Assert.True(await processor.ProcessNextAsync());
            Assert.True(await processor.ProcessNextAsync());
            Assert.False(await processor.ProcessNextAsync());

            var first = Assert.IsType<TextDerivative>(catalog.ReadTextDerivative(text.ItemId, 1));
            var second = Assert.IsType<TextDerivative>(catalog.ReadTextDerivative(markdown.ItemId, 1));
            Assert.Equal("completed", first.Outcome);
            Assert.Equal(text.SourceId, first.SourceId);
            Assert.Equal(LocalCatalog.DefaultProcessorKey, first.ProcessorKey);
            Assert.Equal(text.Sha256, first.SourceSha256);
            Assert.True(first.ProducedUtc > DateTimeOffset.MinValue);
            Assert.Equal("hello\nworld", first.Content);
            Assert.Null(first.ReasonCode);
            Assert.Equal("# Topic\nReady ✅", second.Content);
            Assert.Equal(markdown.Sha256, second.SourceSha256);
            Assert.All(catalog.ListProcessingJobs(), job => Assert.Equal("completed", job.State));
        }

        using var reopened = LocalCatalog.Open(root);
        Assert.Equal("hello\nworld", reopened.ReadTextDerivative(text.ItemId, 1)?.Content);
        Assert.Equal("# Topic\nReady ✅", reopened.ReadTextDerivative(markdown.ItemId, 1)?.Content);
        Assert.False(await new LocalTextProcessor(reopened).ProcessNextAsync());
        await reopened.VerifyOriginalAsync(text.ItemId);
        await reopened.VerifyOriginalAsync(markdown.ItemId);
    }

    [Fact]
    public async Task UnsupportedFileKindsAndMismatchedSignaturesRemainVisible()
    {
        using var catalog = LocalCatalog.Open(root);
        var binary = await Capture(catalog, "image.png", [137, 80, 78, 71], "image");
        var pdf = await Capture(catalog, "article.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"), "pdf");
        var disguised = await Capture(catalog, "disguised.txt",
            Encoding.ASCII.GetBytes("%PDF-1.7"), "disguised");
        var nul = await Capture(catalog, "nul.md", [65, 0, 66], "nul");
        var processor = new LocalTextProcessor(catalog);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await processor.ProcessNextAsync());
        }

        Assert.False(await processor.ProcessNextAsync());
        Assert.Equal("unsupported_format", catalog.ReadTextDerivative(binary.ItemId, 1)?.ReasonCode);
        Assert.Equal("pdf_not_supported", catalog.ReadTextDerivative(pdf.ItemId, 1)?.ReasonCode);
        Assert.Equal("signature_mismatch", catalog.ReadTextDerivative(disguised.ItemId, 1)?.ReasonCode);
        Assert.Equal("binary_content", catalog.ReadTextDerivative(nul.ItemId, 1)?.ReasonCode);
        Assert.All(catalog.ListProcessingJobs(), job => Assert.Equal("unsupported", job.State));
        await catalog.VerifyOriginalAsync(binary.ItemId);
        await catalog.VerifyOriginalAsync(pdf.ItemId);
    }

    [Fact]
    public async Task InvalidUtf8AndCorruptOriginalsFailWithoutPublishingDerivatives()
    {
        using var catalog = LocalCatalog.Open(root);
        var malformed = await Capture(catalog, "invalid.txt", [0xff, 0xfe, 0x41], "invalid");
        var corrupt = await Capture(catalog, "corrupt.md", Encoding.UTF8.GetBytes("good"), "corrupt");
        var truncated = await Capture(catalog, "truncated.txt", Encoding.UTF8.GetBytes("complete"), "truncated");
        File.WriteAllBytes(ObjectPath(corrupt.Sha256), Encoding.UTF8.GetBytes("bad!"));
        File.WriteAllBytes(ObjectPath(truncated.Sha256), Encoding.UTF8.GetBytes("short"));
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        var states = catalog.ListProcessingJobs();
        Assert.Equal("invalid_utf8",
            states.Single(job => job.ItemId == malformed.ItemId).FailureCode);
        Assert.Equal("corrupt_original",
            states.Single(job => job.ItemId == corrupt.ItemId).FailureCode);
        Assert.Equal("corrupt_original",
            states.Single(job => job.ItemId == truncated.ItemId).FailureCode);
        Assert.All(states, job => Assert.Equal("failed", job.State));
        Assert.Null(catalog.ReadTextDerivative(malformed.ItemId, 1));
        Assert.Null(catalog.ReadTextDerivative(corrupt.ItemId, 1));
        Assert.Null(catalog.ReadTextDerivative(truncated.ItemId, 1));
        Assert.True(catalog.RetryProcessing(malformed.ItemId, 1,
            LocalCatalog.DefaultProcessorKey));
        Assert.True(await processor.ProcessNextAsync());
        var retry = catalog.ListProcessingJobs().Single(job => job.ItemId == malformed.ItemId);
        Assert.Equal(2, retry.TotalAttempts);
        Assert.Equal("invalid_utf8", retry.FailureCode);
        Assert.Null(catalog.ReadTextDerivative(malformed.ItemId, 1));
    }

    [Fact]
    public async Task RevisionsAndRevocationPreserveIndependentSources()
    {
        using var catalog = LocalCatalog.Open(root);
        var first = await Capture(catalog, "first.txt", Encoding.UTF8.GetBytes("shared"), "a1");
        var second = await Capture(catalog, "second.txt", Encoding.UTF8.GetBytes("shared"), "b1");
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());

        var updated = await Capture(catalog, "first.txt", Encoding.UTF8.GetBytes("updated"), "a2");
        Assert.Equal(first.SourceId, updated.SourceId);
        Assert.Equal(first.ItemId, updated.ItemId);
        Assert.Equal(2, updated.Revision);
        Assert.True(await processor.ProcessNextAsync());
        Assert.Equal("shared", catalog.ReadTextDerivative(first.ItemId, 1)?.Content);
        Assert.Equal("updated", catalog.ReadTextDerivative(first.ItemId, 2)?.Content);
        Assert.Equal("shared", catalog.ReadTextDerivative(second.ItemId, 1)?.Content);
        Assert.True(catalog.RevokeSource(first.SourceId));
        Assert.Null(catalog.ReadTextDerivative(first.ItemId, 1));
        Assert.Null(catalog.ReadTextDerivative(first.ItemId, 2));
        Assert.Equal("shared", catalog.ReadTextDerivative(second.ItemId, 1)?.Content);
        await catalog.VerifyOriginalAsync(first.ItemId);
        await catalog.VerifyOriginalAsync(second.ItemId);
    }

    [Fact]
    public async Task SnapshotRestoreRetainsTextAndDoesNotExecutePendingJobs()
    {
        var backup = Path.Combine(workspace, "backup");
        var destination = Path.Combine(workspace, "recovered");
        CapturedOriginal accepted;
        using (var catalog = LocalCatalog.Open(root))
        {
            accepted = await Capture(catalog, "retained.txt", Encoding.UTF8.GetBytes("saved"), "a");
            Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
            await catalog.BackupAsync(backup, 20_000_000);
        }

        Assert.Equal(1, (await LocalCatalog.VerifyBackupAsync(backup, 20_000_000)).ObjectCount);
        await LocalCatalog.RestoreAsync(backup, destination, 20_000_000);
        using var restored = LocalCatalog.Open(destination);
        Assert.Equal(8, restored.SchemaVersion);
        Assert.Equal("saved", restored.ReadTextDerivative(accepted.ItemId, 1)?.Content);
        Assert.False(await new LocalTextProcessor(restored).ProcessNextAsync());
        await restored.VerifyOriginalAsync(accepted.ItemId);
    }

    [Fact]
    public async Task FileSizeLimitAndOtherProcessorsAreNotAccidentallyConsumed()
    {
        using var catalog = LocalCatalog.Open(root);
        var large = await Capture(catalog, "large.txt",
            new byte[LocalTextProcessor.MaximumTextBytes + 1], "large");
        Assert.True(catalog.QueueProcessing(large.ItemId, 1, "media-v1"));
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.False(await processor.ProcessNextAsync());
        var text = Assert.IsType<TextDerivative>(catalog.ReadTextDerivative(large.ItemId, 1));
        Assert.Equal("unsupported", text.Outcome);
        Assert.Equal("text_too_large", text.ReasonCode);
        Assert.Null(text.Content);
        Assert.Equal("queued",
            catalog.ListProcessingJobs().Single(job => job.ProcessorKey == "media-v1").State);
    }

    [Fact]
    public async Task TextClaimsDoNotExpireJobsOwnedByOtherProcessors()
    {
        using var catalog = LocalCatalog.Open(root);
        var item = await Capture(catalog, "scoped.txt", Encoding.UTF8.GetBytes("scoped"), "scope");
        Assert.True(catalog.QueueProcessing(item.ItemId, 1, "media-v1"));
        var time = new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
        var media = Assert.IsType<ProcessingLease>(catalog.TryClaimProcessing(
            TimeSpan.FromSeconds(1), maxAttempts: 1, nowUtc: time, processorKey: "media-v1"));
        Assert.Equal("media-v1", media.ProcessorKey);

        var text = Assert.IsType<ProcessingLease>(catalog.TryClaimProcessing(
            TimeSpan.FromSeconds(1), maxAttempts: 1, nowUtc: time.AddSeconds(2),
            processorKey: LocalCatalog.DefaultProcessorKey));
        Assert.Equal(LocalCatalog.DefaultProcessorKey, text.ProcessorKey);
        Assert.Equal("running", catalog.ListProcessingJobs()
            .Single(job => job.ProcessorKey == "media-v1").State);
    }

    [Fact]
    public async Task CanceledWorkCannotProduceTextWithoutExplicitRetry()
    {
        using var catalog = LocalCatalog.Open(root);
        var item = await Capture(catalog, "canceled.txt", Encoding.UTF8.GetBytes("private"), "cancel");
        Assert.True(catalog.CancelProcessing(item.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        Assert.False(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.Null(catalog.ReadTextDerivative(item.ItemId, 1));
        Assert.True(catalog.RetryProcessing(item.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.Equal("private", catalog.ReadTextDerivative(item.ItemId, 1)?.Content);
    }

    [Fact]
    public void SchemaThreeCatalogMigratesWithoutDiscardingProcessingJobs()
    {
        var path = Path.Combine(root, "catalog.sqlite");
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            database.Open();
            foreach (var schema in new[]
                { "LocalCatalog.sql", "LocalCatalog.V2.sql", "LocalCatalog.V3.sql" })
            {
                using var input = typeof(LocalCatalog).Assembly
                    .GetManifestResourceStream("Praxis.Storage." + schema)!;
                using var reader = new StreamReader(input);
                using var command = database.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            using var version = database.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(3L, (long)version.ExecuteScalar()!);
        }

        using var upgraded = LocalCatalog.Open(root);
        Assert.Equal(8, upgraded.SchemaVersion);
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = upgraded.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM text_derivatives";
        Assert.Equal(0L, (long)query.ExecuteScalar()!);
    }


    [Theory]
    [InlineData("parser.cs", "public sealed class C3Reader { public void ParseVertex() {} }")]
    [InlineData("engine.cpp", "void ParseTriangleMesh() { /* source only */ }")]
    [InlineData("symbols.asm", "ParseTriangleMesh PROC\n MOV EAX, 1\n RET\nParseTriangleMesh ENDP")]
    [InlineData("model.lst", "SYMBOL ParseTriangleMesh at section text")]
    public async Task TechnicalTextIsIndexedWithVerifiedOriginalProvenance(
        string name, string content)
    {
        using var catalog = LocalCatalog.Open(root);
        var source = await Capture(catalog, name, Encoding.UTF8.GetBytes(content), "technical");
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());

        var derivative = Assert.IsType<TextDerivative>(
            catalog.ReadTextDerivative(source.ItemId, source.Revision));
        Assert.Equal("completed", derivative.Outcome);
        Assert.Equal(content, derivative.Content);
        Assert.Equal(source.Sha256, derivative.SourceSha256);
        var hit = Assert.Single(catalog.SearchText("ParseTriangleMesh").Where(
            hit => hit.SourceId == source.SourceId));
        Assert.Equal(source.ItemId, hit.ItemId);
        Assert.Equal(source.Sha256, hit.OriginalSha256);
        await catalog.VerifyOriginalAsync(source.ItemId);
    }

    [Fact]
    public async Task PreviouslyUnsupportedCodeIsReprocessedWithoutNewRevisionOrDuplicateIndex()
    {
        CapturedOriginal source;
        using (var catalog = LocalCatalog.Open(root))
        {
            source = await Capture(catalog, "legacy.cs",
                Encoding.UTF8.GetBytes("public class RecoveredTechnicalSource {}"), "legacy");
            SimulateOldUnsupportedText(catalog.DatabasePath, source);
            Assert.Equal("unsupported",
                catalog.ReadTextDerivative(source.ItemId, 1)?.Outcome);
        }

        using (var reopened = LocalCatalog.Open(root))
        {
            var processor = new LocalTextProcessor(reopened);
            Assert.True(await processor.ProcessNextAsync());
            Assert.False(await processor.ProcessNextAsync());
            Assert.Equal("completed",
                reopened.ReadTextDerivative(source.ItemId, 1)?.Outcome);
            Assert.Equal(1, Assert.Single(reopened.SearchText("RecoveredTechnicalSource")).Revision);
            Assert.Single(reopened.SearchText("RecoveredTechnicalSource"));
            Assert.Equal(1, reopened.ListProcessingJobs().Single().TotalAttempts);
            await reopened.VerifyOriginalAsync(source.ItemId);
        }

        using var final = LocalCatalog.Open(root);
        Assert.Single(final.SearchText("RecoveredTechnicalSource"));
        Assert.False(await new LocalTextProcessor(final).ProcessNextAsync());
    }

    [Fact]
    public async Task ReprocessingDoesNotReviveRevokedOrSupersededText()
    {
        using var catalog = LocalCatalog.Open(root);
        var revoked = await Capture(catalog, "revoked.cs",
            Encoding.UTF8.GetBytes("RevokedTechnicalSource"), "revoked");
        var first = await Capture(catalog, "updated.cs",
            Encoding.UTF8.GetBytes("SupersededTechnicalSource"), "old");
        SimulateOldUnsupportedText(catalog.DatabasePath, revoked);
        SimulateOldUnsupportedText(catalog.DatabasePath, first);
        Assert.True(catalog.RevokeSource(revoked.SourceId));
        var revised = await Capture(catalog, "updated.cs",
            Encoding.UTF8.GetBytes("CurrentTechnicalSource"), "new");
        Assert.Equal(2, revised.Revision);

        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.False(await processor.ProcessNextAsync());
        Assert.Empty(catalog.SearchText("RevokedTechnicalSource"));
        Assert.Empty(catalog.SearchText("SupersededTechnicalSource"));
        Assert.Single(catalog.SearchText("CurrentTechnicalSource"));
        Assert.Equal("unsupported",
            catalog.ReadTextDerivative(first.ItemId, 1)?.Outcome);
    }

    [Theory]
    [InlineData("forged.cs", new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x41 })]
    [InlineData("forged.asm", new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x41 })]
    [InlineData("broken.cpp", new byte[] { 0xff, 0xfe, 0x41 })]
    public async Task CodeFileExtensionsDoNotOverrideBinaryOrMalformedContent(
        string name, byte[] bytes)
    {
        using var catalog = LocalCatalog.Open(root);
        var source = await Capture(catalog, name, bytes, "hostile");
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        var derivative = catalog.ReadTextDerivative(source.ItemId, 1);
        if (name == "broken.cpp")
        {
            Assert.Null(derivative);
            Assert.Equal("invalid_utf8", Assert.Single(catalog.ListProcessingJobs()).FailureCode);
        }
        else
        {
            Assert.Equal("signature_mismatch", derivative?.ReasonCode);
        }

        Assert.Empty(catalog.SearchText("hostile"));
        await catalog.VerifyOriginalAsync(source.ItemId);
    }

    private static void SimulateOldUnsupportedText(string databasePath, CapturedOriginal source)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        database.Open();
        using var transaction = database.BeginTransaction();
        using var job = database.CreateCommand();
        job.Transaction = transaction;
        job.CommandText = """
            UPDATE processing_jobs SET state = 'unsupported'
            WHERE item_id = $item AND revision = $revision
              AND processor_key = 'source-text-v1' AND state = 'queued'
            """;
        job.Parameters.AddWithValue("$item", source.ItemId.ToString("D"));
        job.Parameters.AddWithValue("$revision", source.Revision);
        Assert.Equal(1, job.ExecuteNonQuery());
        using var derivative = database.CreateCommand();
        derivative.Transaction = transaction;
        derivative.CommandText = """
            INSERT INTO text_derivatives
                (item_id, revision, processor_key, source_digest,
                 outcome, content, reason_code, created_utc)
            VALUES ($item, $revision, 'source-text-v1', $digest,
                    'unsupported', NULL, 'unsupported_format', $now)
            """;
        derivative.Parameters.AddWithValue("$item", source.ItemId.ToString("D"));
        derivative.Parameters.AddWithValue("$revision", source.Revision);
        derivative.Parameters.AddWithValue("$digest", source.Sha256);
        derivative.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(1, derivative.ExecuteNonQuery());
        transaction.Commit();
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string name, byte[] bytes, string key)
    {
        var locator = Path.Combine(workspace, "incoming", name);
        using var input = new MemoryStream(bytes);
        return await catalog.CaptureFileAsync(locator, input, bytes.Length, key);
    }

    private string ObjectPath(string digest) => Path.Combine(
        root, "objects", digest[..2], digest[2..4], digest);

    private static readonly UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
