using System.Text;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalMultiDomainEvidenceTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-evidence-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LocalMultiDomainEvidenceTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(dataRoot, privateMode);
        }
    }

    [Fact]
    public async Task LocalBudgetIncludesExactRowsWithoutGuessingPendingOrTransfers()
    {
        const string csv = """
            date,record_id,kind,amount,currency,category,description,status
            2026-11-01,pay-1,income,4500.00,USD,Income,Salary,posted
            2026-11-02,rent-1,expense,1350.00,USD,Housing,Rent,posted
            2026-11-03,bill-1,bill,140.00,USD,Utilities,Next utility bill,estimated
            2026-11-04,shop-1,expense,450.00,USD,Groceries,"Shop, week one",posted
            2026-11-05,refund-1,refund,50.00,USD,Groceries,Store return,posted
            2026-11-06,pending-1,expense,70.00,USD,Shopping,Pending order,pending
            2026-11-06,transfer-1,transfer,900.00,USD,Transfer,Between own accounts,posted
            2026-10-07,old-1,expense,300.00,USD,Other,Last month,posted
            2026-11-01,pay-1,income,4500.00,USD,Income,Salary,posted
            """;
        CapturedOriginal captured;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            captured = await Capture(catalog, "owner-budget.csv", csv, "budget");
            var result = await new LocalMonthlyBudgetAnalyzer(catalog)
                .AnalyzeAsync(captured.SourceId, 2026, 11);
            Assert.Equal(4500m, result.PostedIncome);
            Assert.Equal(1750m, result.PostedSpending);
            Assert.Equal(140m, result.PlannedBills);
            Assert.Equal(2750m, result.ActualBalance);
            Assert.Equal(2610m, result.BalanceAfterPlannedBills);
            Assert.Equal(70m, result.PendingNetSpending);
            Assert.Equal(6, result.IncludedRows);
            Assert.Equal(1, result.IgnoredDuplicateRows);
            Assert.Equal(1, result.PendingRows);
            Assert.Equal(1, result.TransfersExcluded);
            Assert.Equal("USD", result.Currency);
            Assert.Equal(captured.ItemId, result.ItemId);
            Assert.Equal(captured.Sha256, result.SourceSha256);
            Assert.Equal(4, result.Evidence.Count(entry => entry.Status == "posted"));
            Assert.Contains(result.Evidence,
                entry => entry.SourceRow == 5 && entry.Description == "Shop, week one");
            Assert.Equal(400m, result.Categories.Single(
                category => category.Category == "Groceries").PostedSpending);
            Assert.Equal(140m, result.Categories.Single(
                category => category.Category == "Utilities").PlannedBills);
            Assert.Empty(catalog.SearchText("Salary"));
            await catalog.VerifyOriginalAsync(captured.ItemId);
        }

        using var reopened = LocalCatalog.Open(dataRoot);
        var again = await new LocalMonthlyBudgetAnalyzer(reopened)
            .AnalyzeAsync(captured.SourceId, 2026, 11);
        Assert.Equal(2610m, again.BalanceAfterPlannedBills);
        Assert.Equal(captured.Sha256, again.SourceSha256);
        Assert.Equal(0m, (await new LocalMonthlyBudgetAnalyzer(reopened)
            .AnalyzeAsync(captured.SourceId, 2026, 9)).PostedIncome);
    }

    [Theory]
    [InlineData("2026-11-01,same,income,100.00,USD,Income,Salary,posted\n2026-11-01,same,income,200.00,USD,Income,Salary,posted")]
    [InlineData("2026-11-01,one,expense,-9.00,USD,Food,Expense,posted")]
    [InlineData("2026-11-01,one,expense,1.001,USD,Food,Expense,posted")]
    [InlineData("2026-11-01,one,expense,1.00,EUR,Food,Expense,posted")]
    [InlineData("2026-11-31,one,expense,1.00,USD,Food,Expense,posted")]
    [InlineData("2026-11-01,one,bill,1.00,USD,Rent,Rent,posted")]
    [InlineData("2026-11-01,one,expense,1.00,USD,Rent,\"unclosed,posted")]
    public async Task BudgetRejectsAmbiguousOrMalformedEvidence(string row)
    {
        const string header =
            "date,record_id,kind,amount,currency,category,description,status\n";
        using var catalog = LocalCatalog.Open(dataRoot);
        var captured = await Capture(catalog, "invalid.csv", header + row, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new LocalMonthlyBudgetAnalyzer(catalog)
                .AnalyzeAsync(captured.SourceId, 2026, 11));
        await catalog.VerifyOriginalAsync(captured.ItemId);
    }

    [Fact]
    public async Task BudgetRefusesRevokedRevisedAndCorruptOriginals()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        const string prefix = "date,record_id,kind,amount,currency,category,description,status\n";
        var initial = await Capture(catalog, "finance.csv",
            prefix + "2026-11-01,a,income,400.00,USD,Income,Old,posted\n", "initial");
        Assert.Equal(400m, (await new LocalMonthlyBudgetAnalyzer(catalog)
            .AnalyzeAsync(initial.SourceId, 2026, 11)).PostedIncome);
        var changed = await Capture(catalog, "finance.csv",
            prefix + "2026-11-01,b,income,500.00,USD,Income,New,posted\n", "changed");
        Assert.Equal(initial.ItemId, changed.ItemId);
        Assert.Equal(2, changed.Revision);
        var report = await new LocalMonthlyBudgetAnalyzer(catalog)
            .AnalyzeAsync(changed.SourceId, 2026, 11);
        Assert.Equal(changed.Sha256, report.SourceSha256);
        Assert.Equal(500m, report.PostedIncome);
        Assert.True(catalog.RevokeSource(changed.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new LocalMonthlyBudgetAnalyzer(catalog)
                .AnalyzeAsync(changed.SourceId, 2026, 11));
    }

    [Fact]
    public async Task BudgetRejectsForgedBytesBeforeParsing()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var original = await Capture(catalog, "bad.csv",
            "date,record_id,kind,amount,currency,category,description,status\n", "bad");
        var path = Path.Combine(dataRoot, "objects", original.Sha256[..2],
            original.Sha256[2..4], original.Sha256);
        File.WriteAllBytes(path, [0, 255, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new LocalMonthlyBudgetAnalyzer(catalog)
                .AnalyzeAsync(original.SourceId, 2026, 11));
    }

    [Theory]
    [InlineData(".vtt", "WEBVTT\n\n00:00:05.000 --> 00:00:08.000\nUse a violet soldering jig.\n\n00:00:09.000 --> 00:00:12.000\nUnplug the iron.\n")]
    [InlineData(".srt", "1\n00:00:05,000 --> 00:00:08,000\nUse a violet soldering jig.\n\n2\n00:00:09,000 --> 00:00:12,000\nUnplug the iron.\n")]
    public async Task DigestBoundVideoCaptionReturnsExactTimeAndFullMediaIdentity(
        string extension, string caption)
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var media = await Capture(catalog, "tutorial.mp4", "video fixture", "media");
        var sidecar = await Capture(catalog,
            "tutorial.mp4." + media.Sha256 + extension, caption, "captions");
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        var cue = Assert.Single(catalog.SearchVideoCues("violet"));
        Assert.Equal(5_000L, cue.StartMilliseconds);
        Assert.Equal(8_000L, cue.EndMilliseconds);
        Assert.Equal("Use a violet soldering jig.", cue.CaptionText);
        Assert.Equal("timed-caption-v1", cue.Producer);
        Assert.Equal(sidecar.SourceId, cue.CaptionSourceId);
        Assert.Equal(sidecar.Sha256, cue.CaptionSha256);
        Assert.Equal(media.ItemId, cue.VideoItemId);
        Assert.Equal(media.Sha256, cue.VideoSha256);
        Assert.Equal(caption.Substring(cue.CaptionOffset, cue.CaptionLength), cue.CaptionText);
        Assert.Empty(catalog.SearchVideoCues("violet unplug"));
        Assert.Single(catalog.SearchText("violet"));
        await catalog.VerifyOriginalAsync(media.ItemId);
    }

    [Fact]
    public async Task VideoRevisionOrRevocationNeverReusesStaleCaptionTimestamp()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var video = await Capture(catalog, "tutorial.mp4", "video-v1", "video-v1");
        const string caption = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nViolet physics demonstration.\n";
        var cues = await Capture(catalog,
            "tutorial.mp4." + video.Sha256 + ".vtt", caption, "captions");
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.Single(catalog.SearchVideoCues("Violet"));

        var replacement = await Capture(catalog, "tutorial.mp4", "video-v2", "video-v2");
        Assert.Equal(2, replacement.Revision);
        Assert.Empty(catalog.SearchVideoCues("Violet"));

        var newCues = await Capture(catalog,
            "tutorial.mp4." + replacement.Sha256 + ".vtt", caption, "new-captions");
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.Equal(newCues.SourceId,
            Assert.Single(catalog.SearchVideoCues("Violet")).CaptionSourceId);

        Assert.True(catalog.RevokeSource(replacement.SourceId));
        Assert.Empty(catalog.SearchVideoCues("Violet"));
        Assert.True(catalog.RevokeSource(cues.SourceId));
        await catalog.VerifyOriginalAsync(video.ItemId);
    }

    [Fact]
    public async Task VideoRequiresExplicitHashBindingAndValidCaptionSyntax()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var video = await Capture(catalog, "tutorial.mp4", "video", "video");
        await Capture(catalog, "tutorial.vtt",
            "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nViolet topic.\n", "legacy");
        await Capture(catalog, "tutorial.mp4." + video.Sha256 + ".srt",
            "1\n00:00:10,000 --> 00:00:02,000\nViolet invalid timing.\n", "invalid");
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.Empty(catalog.SearchVideoCues("Violet"));
    }


    [Fact]
    public async Task PrivateDesktopProtocolReturnsGroundedVideoCuesAndMonthlyBudget()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var video = await Capture(catalog, "clip.mp4", "video content", "video");
        var captions = await Capture(catalog, "clip.mp4." + video.Sha256 + ".vtt",
            "WEBVTT\\n\\n00:00:03.000 --> 00:00:06.000\\nViolet assembly instruction.\\n",
            "captions");
        var finance = await Capture(catalog, "monthly.csv",
            "date,record_id,kind,amount,currency,category,description,status\\n" +
            "2026-11-01,pay,income,1100.00,USD,Income,Salary,posted\\n" +
            "2026-11-02,food,expense,100.00,USD,Groceries,Food,posted\\n",
            "financial");
        var worker = new LocalTextProcessor(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        var protocol = new LocalDesktopProtocol(catalog);
        using (var clipResult = System.Text.Json.JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"clip","action":"mediaCues","query":"violet"}""")))
        {
            Assert.True(clipResult.RootElement.GetProperty("ok").GetBoolean());
            var cue = Assert.Single(clipResult.RootElement.GetProperty("data")
                .GetProperty("clips").EnumerateArray());
            Assert.Equal(video.SourceId.ToString("D"),
                cue.GetProperty("videoSourceId").GetString());
            Assert.Equal(captions.Sha256, cue.GetProperty("captionSha256").GetString());
            Assert.Equal(3000L, cue.GetProperty("startMilliseconds").GetInt64());
        }

        using (var reportResult = System.Text.Json.JsonDocument.Parse(
            await protocol.HandleAsync($$"""
                {"requestId":"money","action":"budgetSummary","sourceId":"{{finance.SourceId:D}}","year":2026,"month":11}
                """)))
        {
            Assert.True(reportResult.RootElement.GetProperty("ok").GetBoolean());
            var report = reportResult.RootElement.GetProperty("data")
                .GetProperty("budget");
            Assert.Equal(finance.Sha256, report.GetProperty("sourceSha256").GetString());
            Assert.Equal(1100m, report.GetProperty("postedIncome").GetDecimal());
            Assert.Equal(100m, report.GetProperty("postedSpending").GetDecimal());
            Assert.Equal(1000m, report.GetProperty("actualBalance").GetDecimal());
        }

        Assert.True(catalog.RevokeSource(finance.SourceId));
        using var revoked = System.Text.Json.JsonDocument.Parse(
            await protocol.HandleAsync($$"""
                {"requestId":"revoked","action":"budgetSummary","sourceId":"{{finance.SourceId:D}}","year":2026,"month":11}
                """));
        Assert.False(revoked.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(revoked.RootElement.TryGetProperty("data", out _));

        using var denied = System.Text.Json.JsonDocument.Parse(
            await protocol.HandleAsync("""
                {"requestId":"wrong","action":"budgetSummary","sourceId":"not-an-id","year":2026,"month":11}
                """));
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BudgetCsvHandlesQuotedLineBreaksWithoutChangingPhysicalRowCitations()
    {
        var csv = "date,record_id,kind,amount,currency,category,description,status\\r\\n" +
            "2026-11-01,one,expense,10.00,USD,Home,\"Two-line\\nreceipt\",posted\\r\\n" +
            "2026-11-02,two,expense,5.00,USD,Home,Second purchase,posted\\r\\n";
        using var catalog = LocalCatalog.Open(dataRoot);
        var source = await Capture(catalog, "quoted.csv", csv, "quoted");
        var report = await new LocalMonthlyBudgetAnalyzer(catalog)
            .AnalyzeAsync(source.SourceId, 2026, 11);
        Assert.Equal(15m, report.PostedSpending);
        Assert.Equal(new[] { 2, 4 }, report.Evidence.Select(e => e.SourceRow).ToArray());
        Assert.Equal("Two-line\\nreceipt", report.Evidence[0].Description);
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, string contents, string requestKey)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        using var source = new MemoryStream(bytes);
        return await catalog.CaptureFileAsync(
            Path.Combine(workspace, "incoming", filename), source,
            bytes.Length + 1, requestKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
