using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Processing;
using Praxis.Domain.Originals;
using Praxis.Domain.Processing;
using Praxis.Domain.Search;
using Praxis.Domain.Text;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Persistence.Recovery;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Processing;

public sealed class TextExtractionTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        TestPaths.Temp, "praxis-text-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public TextExtractionTests()
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
        byte[] markdownBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("# Topic\nReady ✅")).ToArray();
        using (SqliteCatalog catalog = SqliteCatalog.Open(root))
        {
            text = await Capture(catalog, "plain.txt", Encoding.UTF8.GetBytes("hello\nworld"), "text");
            markdown = await Capture(catalog, "note.md", markdownBytes, "markdown");
            ProcessingDriver processor = ProcessingDriver.Text(catalog);
            Assert.True(await processor.ProcessNextAsync());
            Assert.True(await processor.ProcessNextAsync());
            Assert.False(await processor.ProcessNextAsync());

            TextDerivative first = Assert.IsType<TextDerivative>(catalog.Text.Read(text.ItemId, 1));
            TextDerivative second = Assert.IsType<TextDerivative>(catalog.Text.Read(markdown.ItemId, 1));
            Assert.Equal("completed", first.Outcome);
            Assert.Equal(text.SourceId, first.SourceId);
            Assert.Equal(ProcessorKeys.SourceText, first.ProcessorKey);
            Assert.Equal(text.Sha256, first.SourceSha256);
            Assert.True(first.ProducedUtc > DateTimeOffset.MinValue);
            Assert.Equal("hello\nworld", first.Content);
            Assert.Null(first.ReasonCode);
            Assert.Equal("# Topic\nReady ✅", second.Content);
            Assert.Equal(markdown.Sha256, second.SourceSha256);
            IReadOnlyList<ProcessingJob> jobs = catalog.Processing.List();
            Assert.All(jobs.Where(job => job.ProcessorKey == ProcessorKeys.SourceText),
                job => Assert.Equal("completed", job.State));
            // Completed extraction schedules exactly one downstream segmentation job per revision.
            Assert.Equal(2, jobs.Count(job =>
                job.ProcessorKey == ProcessorKeys.TextSegments && job.State == "queued"));
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(root);
        Assert.Equal("hello\nworld", reopened.Text.Read(text.ItemId, 1)?.Content);
        Assert.Equal("# Topic\nReady ✅", reopened.Text.Read(markdown.ItemId, 1)?.Content);
        Assert.False(await ProcessingDriver.Text(reopened).ProcessNextAsync());
        await reopened.Originals.VerifyAsync(text.ItemId);
        await reopened.Originals.VerifyAsync(markdown.ItemId);
    }

    [Fact]
    public async Task UnsupportedFileKindsAndMismatchedSignaturesRemainVisible()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal binary = await Capture(catalog, "archive.zip", [0x50, 0x4b, 0x03, 0x04, 0x00], "archive");
        CapturedOriginal pdf = await Capture(catalog, "article.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"), "pdf");
        CapturedOriginal disguised = await Capture(catalog, "disguised.txt",
            Encoding.ASCII.GetBytes("%PDF-1.7"), "disguised");
        CapturedOriginal nul = await Capture(catalog, "nul.md", [65, 0, 66], "nul");
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(await processor.ProcessNextAsync());
        }

        Assert.False(await processor.ProcessNextAsync());
        Assert.Equal("unsupported_format", catalog.Text.Read(binary.ItemId, 1)?.ReasonCode);
        // Files are read by their content signature, so a PDF named .txt is (attempted as) a PDF.
        Assert.StartsWith("pdf_", catalog.Text.Read(pdf.ItemId, 1)?.ReasonCode);
        Assert.StartsWith("pdf_", catalog.Text.Read(disguised.ItemId, 1)?.ReasonCode);
        Assert.Equal("binary_content", catalog.Text.Read(nul.ItemId, 1)?.ReasonCode);
        Assert.All(catalog.Processing.List().Where(job => job.ProcessorKey == ProcessorKeys.SourceText), job => Assert.Equal("unsupported", job.State));
        await catalog.Originals.VerifyAsync(binary.ItemId);
        await catalog.Originals.VerifyAsync(pdf.ItemId);
    }

    [Fact]
    public async Task InvalidUtf8AndCorruptOriginalsFailWithoutPublishingDerivatives()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal malformed = await Capture(catalog, "invalid.txt", [0xff, 0xfe, 0x41], "invalid");
        CapturedOriginal corrupt = await Capture(catalog, "corrupt.md", Encoding.UTF8.GetBytes("good"), "corrupt");
        CapturedOriginal truncated = await Capture(catalog, "truncated.txt", Encoding.UTF8.GetBytes("complete"), "truncated");
        File.WriteAllBytes(ObjectPath(corrupt.Sha256), Encoding.UTF8.GetBytes("bad!"));
        File.WriteAllBytes(ObjectPath(truncated.Sha256), Encoding.UTF8.GetBytes("short"));
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        IReadOnlyList<ProcessingJob> states = catalog.Processing.List().Where(job => job.ProcessorKey == ProcessorKeys.SourceText).ToArray();

        // Undecodable bytes are not text: closed with their reason, never guessed.
        Assert.Equal("binary_content", catalog.Text.Read(malformed.ItemId, 1)?.ReasonCode);
        Assert.Equal("unsupported", states.Single(job => job.ItemId == malformed.ItemId).State);
        Assert.Equal("corrupt_original",
            states.Single(job => job.ItemId == corrupt.ItemId).FailureCode);
        Assert.Equal("corrupt_original",
            states.Single(job => job.ItemId == truncated.ItemId).FailureCode);
        Assert.All(states.Where(job => job.ItemId != malformed.ItemId), job => Assert.Equal("failed", job.State));
        Assert.Null(catalog.Text.Read(corrupt.ItemId, 1));
        Assert.Null(catalog.Text.Read(truncated.ItemId, 1));
        Assert.True(catalog.Processing.Retry(corrupt.ItemId, 1,
            ProcessorKeys.SourceText));
        Assert.True(await processor.ProcessNextAsync());
        ProcessingJob retry = catalog.Processing.List().Single(job => job.ItemId == corrupt.ItemId && job.ProcessorKey == ProcessorKeys.SourceText);
        Assert.Equal(2, retry.TotalAttempts);
        Assert.Equal("corrupt_original", retry.FailureCode);
        Assert.Null(catalog.Text.Read(corrupt.ItemId, 1));
    }

    [Fact]
    public async Task RevisionsAndRevocationPreserveIndependentSources()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal first = await Capture(catalog, "first.txt", Encoding.UTF8.GetBytes("shared"), "a1");
        CapturedOriginal second = await Capture(catalog, "second.txt", Encoding.UTF8.GetBytes("shared"), "b1");
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());

        CapturedOriginal updated = await Capture(catalog, "first.txt", Encoding.UTF8.GetBytes("updated"), "a2");
        Assert.Equal(first.SourceId, updated.SourceId);
        Assert.Equal(first.ItemId, updated.ItemId);
        Assert.Equal(2, updated.Revision);
        Assert.True(await processor.ProcessNextAsync());
        Assert.Equal("shared", catalog.Text.Read(first.ItemId, 1)?.Content);
        Assert.Equal("updated", catalog.Text.Read(first.ItemId, 2)?.Content);
        Assert.Equal("shared", catalog.Text.Read(second.ItemId, 1)?.Content);
        Assert.True(catalog.Sources.Revoke(first.SourceId));
        Assert.Null(catalog.Text.Read(first.ItemId, 1));
        Assert.Null(catalog.Text.Read(first.ItemId, 2));
        Assert.Equal("shared", catalog.Text.Read(second.ItemId, 1)?.Content);
        await catalog.Originals.VerifyAsync(first.ItemId);
        await catalog.Originals.VerifyAsync(second.ItemId);
    }

    [Fact]
    public async Task SnapshotRestoreRetainsTextAndDoesNotExecutePendingJobs()
    {
        string backup = Path.Combine(workspace, "backup");
        string destination = Path.Combine(workspace, "recovered");
        CapturedOriginal accepted;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root))
        {
            accepted = await Capture(catalog, "retained.txt", Encoding.UTF8.GetBytes("saved"), "a");
            Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            await catalog.Recovery.BackupAsync(backup, 20_000_000);
        }

        Assert.Equal(1, (await CatalogSnapshots.VerifyAsync(backup, 20_000_000)).ObjectCount);
        await CatalogSnapshots.RestoreAsync(backup, destination, 20_000_000);
        using SqliteCatalog restored = SqliteCatalog.Open(destination);
        Assert.Equal(CatalogSchema.CurrentVersion, restored.SchemaVersion);
        Assert.Equal("saved", restored.Text.Read(accepted.ItemId, 1)?.Content);
        Assert.False(await ProcessingDriver.Text(restored).ProcessNextAsync());
        await restored.Originals.VerifyAsync(accepted.ItemId);
    }

    [Fact]
    public async Task FileSizeLimitAndOtherProcessorsAreNotAccidentallyConsumed()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal large = await Capture(catalog, "large.txt",
            Encoding.ASCII.GetBytes(new string('a', TextLimits.MaximumTextBytes + 10)), "large");
        Assert.True(catalog.Processing.Queue(large.ItemId, 1, "media-v1"));
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.False(await processor.ProcessNextAsync());
        TextDerivative text = Assert.IsType<TextDerivative>(catalog.Text.Read(large.ItemId, 1));
        // Long text is kept up to the stored bound rather than rejected; intake records that it was truncated.
        Assert.Equal("completed", text.Outcome);
        Assert.Equal(TextLimits.MaximumTextBytes, text.Content!.Length);
        Assert.Equal("queued",
            catalog.Processing.List().Single(job => job.ProcessorKey == "media-v1").State);
    }

    [Fact]
    public async Task TextClaimsDoNotExpireJobsOwnedByOtherProcessors()
    {
        DateTimeOffset time = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
        ManualTimeProvider clock = new(time);
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        CapturedOriginal item = await Capture(catalog, "scoped.txt", Encoding.UTF8.GetBytes("scoped"), "scope");
        Assert.True(catalog.Processing.Queue(item.ItemId, 1, "media-v1"));
        ProcessingLease media = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromSeconds(1), maxAttempts: 1, processorKey: "media-v1"));
        Assert.Equal("media-v1", media.ProcessorKey);

        clock.UtcNow = time.AddSeconds(2);
        ProcessingLease text = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromSeconds(1), maxAttempts: 1, processorKey: ProcessorKeys.SourceText));
        Assert.Equal(ProcessorKeys.SourceText, text.ProcessorKey);
        Assert.Equal("running", catalog.Processing.List()
            .Single(job => job.ProcessorKey == "media-v1").State);
    }

    [Fact]
    public async Task CanceledWorkCannotProduceTextWithoutExplicitRetry()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal item = await Capture(catalog, "canceled.txt", Encoding.UTF8.GetBytes("private"), "cancel");
        Assert.True(catalog.Processing.Cancel(item.ItemId, 1, ProcessorKeys.SourceText));
        Assert.False(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Null(catalog.Text.Read(item.ItemId, 1));
        Assert.True(catalog.Processing.Retry(item.ItemId, 1, ProcessorKeys.SourceText));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Equal("private", catalog.Text.Read(item.ItemId, 1)?.Content);
    }

    [Fact]
    public void SchemaThreeCatalogMigratesWithoutDiscardingProcessingJobs()
    {
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection database = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            database.Open();
            foreach (string schema in new[]
                { "Catalog.V1.sql", "Catalog.V2.sql", "Catalog.V3.sql" })
            {
                using Stream input = typeof(SqliteCatalog).Assembly
                    .GetManifestResourceStream("Praxis.Infrastructure." + schema)!;
                using StreamReader reader = new(input);
                using SqliteCommand command = database.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            using SqliteCommand version = database.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(3L, (long)version.ExecuteScalar()!);
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(root);
        Assert.Equal(CatalogSchema.CurrentVersion, upgraded.SchemaVersion);
        using SqliteConnection check = new(new SqliteConnectionStringBuilder
        {
            DataSource = upgraded.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        check.Open();
        using SqliteCommand query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM text_derivatives";
        Assert.Equal(0L, (long)query.ExecuteScalar()!);
    }


    [Theory]
    [InlineData("parser.cs", "public sealed class C3Reader { public void ParseTriangleMesh() {} }")]
    [InlineData("engine.cpp", "void ParseTriangleMesh() { /* source only */ }")]
    [InlineData("symbols.asm", "ParseTriangleMesh PROC\n MOV EAX, 1\n RET\nParseTriangleMesh ENDP")]
    [InlineData("model.lst", "SYMBOL ParseTriangleMesh at section text")]
    public async Task TechnicalTextIsIndexedWithVerifiedOriginalProvenance(
        string name, string content)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal source = await Capture(catalog, name, Encoding.UTF8.GetBytes(content), "technical");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());

        TextDerivative derivative = Assert.IsType<TextDerivative>(
            catalog.Text.Read(source.ItemId, source.Revision));
        Assert.Equal("completed", derivative.Outcome);
        Assert.Equal(content, derivative.Content);
        Assert.Equal(source.Sha256, derivative.SourceSha256);
        SearchHit hit = Assert.Single(catalog.Search.Query("ParseTriangleMesh"),
            result => result.SourceId == source.SourceId);
        Assert.Equal(source.ItemId, hit.ItemId);
        Assert.Equal(source.Sha256, hit.OriginalSha256);
        await catalog.Originals.VerifyAsync(source.ItemId);
    }

    [Fact]
    public async Task PreviouslyUnsupportedCodeIsReprocessedWithoutNewRevisionOrDuplicateIndex()
    {
        CapturedOriginal source;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root))
        {
            source = await Capture(catalog, "legacy.cs",
                Encoding.UTF8.GetBytes("public class RecoveredTechnicalSource {}"), "legacy");
            SimulateOldUnsupportedText(catalog.DatabasePath, source);
            Assert.Equal("unsupported",
                catalog.Text.Read(source.ItemId, 1)?.Outcome);
        }

        using (SqliteCatalog reopened = SqliteCatalog.Open(root))
        {
            ProcessingDriver processor = ProcessingDriver.Text(reopened);
            Assert.True(await processor.ProcessNextAsync());
            Assert.False(await processor.ProcessNextAsync());
            Assert.Equal("completed",
                reopened.Text.Read(source.ItemId, 1)?.Outcome);
            Assert.Equal(1, Assert.Single(reopened.Search.Query("RecoveredTechnicalSource")).Revision);
            Assert.Single(reopened.Search.Query("RecoveredTechnicalSource"));
            Assert.Equal(1, reopened.Processing.List()
                .Single(job => job.ProcessorKey == ProcessorKeys.SourceText).TotalAttempts);
            Assert.Equal("queued", reopened.Processing.List()
                .Single(job => job.ProcessorKey == ProcessorKeys.TextSegments).State);
            await reopened.Originals.VerifyAsync(source.ItemId);
        }

        using SqliteCatalog final = SqliteCatalog.Open(root);
        Assert.Single(final.Search.Query("RecoveredTechnicalSource"));
        Assert.False(await ProcessingDriver.Text(final).ProcessNextAsync());
    }

    [Fact]
    public async Task ReprocessingDoesNotReviveRevokedOrSupersededText()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal revoked = await Capture(catalog, "revoked.cs",
            Encoding.UTF8.GetBytes("RevokedTechnicalSource"), "revoked");
        CapturedOriginal first = await Capture(catalog, "updated.cs",
            Encoding.UTF8.GetBytes("SupersededTechnicalSource"), "old");
        SimulateOldUnsupportedText(catalog.DatabasePath, revoked);
        SimulateOldUnsupportedText(catalog.DatabasePath, first);
        Assert.True(catalog.Sources.Revoke(revoked.SourceId));
        CapturedOriginal revised = await Capture(catalog, "updated.cs",
            Encoding.UTF8.GetBytes("CurrentTechnicalSource"), "new");
        Assert.Equal(2, revised.Revision);

        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.False(await processor.ProcessNextAsync());
        Assert.Empty(catalog.Search.Query("RevokedTechnicalSource"));
        Assert.Empty(catalog.Search.Query("SupersededTechnicalSource"));
        Assert.Single(catalog.Search.Query("CurrentTechnicalSource"));
        Assert.Equal("unsupported",
            catalog.Text.Read(first.ItemId, 1)?.Outcome);
    }

    [Theory]
    [InlineData("forged.cs", new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x41 })]
    [InlineData("forged.asm", new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x41 })]
    [InlineData("picture.cs", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })]
    [InlineData("compressed.cs", new byte[] { 0x1f, 0x8b, 0x08, 0x00 })]
    [InlineData("mach.cs", new byte[] { 0xcf, 0xfa, 0xed, 0xfe, 0x00 })]
    [InlineData("universal.cs", new byte[] { 0xca, 0xfe, 0xba, 0xbe, 0x00 })]
    [InlineData("broken.cpp", new byte[] { 0xff, 0xfe, 0x41 })]
    public async Task CodeFileExtensionsDoNotOverrideBinaryOrMalformedContent(
        string name, byte[] bytes)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        CapturedOriginal source = await Capture(catalog, name, bytes, "hostile");
        if (name == "picture.cs")
        {
            // An image is read by what it is: with no OCR engine the read waits instead of decoding pixels as text.
            await Assert.ThrowsAsync<ProcessorUnavailableException>(() => ProcessingDriver.Text(catalog).ProcessNextAsync());
            Assert.Null(catalog.Text.Read(source.ItemId, 1));
        }
        else
        {
            Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            Assert.Equal(name == "broken.cpp" ? "binary_content" : "content_unrecognized", catalog.Text.Read(source.ItemId, 1)?.ReasonCode);
        }

        Assert.Empty(catalog.Search.Query("hostile"));
        await catalog.Originals.VerifyAsync(source.ItemId);
    }

    private static void SimulateOldUnsupportedText(string databasePath, CapturedOriginal source)
    {
        using SqliteConnection database = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        database.Open();
        using SqliteTransaction transaction = database.BeginTransaction();
        using SqliteCommand job = database.CreateCommand();
        job.Transaction = transaction;
        job.CommandText = """
            UPDATE processing_jobs SET state = 'unsupported'
            WHERE item_id = $item AND revision = $revision
              AND processor_key = 'source-text-v1' AND state = 'queued'
            """;
        job.Parameters.AddWithValue("$item", source.ItemId.ToString("D"));
        job.Parameters.AddWithValue("$revision", source.Revision);
        Assert.Equal(1, job.ExecuteNonQuery());
        using SqliteCommand derivative = database.CreateCommand();
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
        SqliteCatalog catalog, string name, byte[] bytes, string key)
    {
        string locator = Path.Combine(workspace, "incoming", name);
        using MemoryStream input = new(bytes);
        return await catalog.Originals.CaptureFileAsync(locator, input, bytes.Length, key);
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
