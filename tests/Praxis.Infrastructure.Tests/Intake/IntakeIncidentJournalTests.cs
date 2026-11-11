using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Persistence.Recovery;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Intake;

public sealed class IntakeIncidentJournalTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-incidents-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public IntakeIncidentJournalTests()
    {
        root = Path.Combine(workspace, "data");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task FaultTransitionsDeduplicateResolveAndSurviveCoherentRestore()
    {
        string inbox = Path.Combine(workspace, "inbox");
        DateTimeOffset start = new(2026, 11, 10, 22, 0, 0, TimeSpan.Zero);
        ManualTimeProvider clock = new(start);
        using (SqliteCatalog catalog = SqliteCatalog.Open(root, clock))
        {
            catalog.IntakeIncidents.RecordState(inbox, "starting");
            Assert.Empty(catalog.IntakeIncidents.Recent(20));
            catalog.IntakeIncidents.RecordState(inbox, "offline");
            catalog.IntakeIncidents.RecordState(inbox, "offline");
            Assert.Single(catalog.IntakeIncidents.Recent(20));
            clock.UtcNow = start.AddMinutes(1);
            catalog.IntakeIncidents.RecordState(inbox, "reapproval_required");
            Assert.Equal(2, catalog.IntakeIncidents.Recent(20).Count);
            Assert.Equal(start.AddMinutes(1), catalog.IntakeIncidents.Recent(20)[1].ResolvedUtc);
            clock.UtcNow = start.AddMinutes(2);
            catalog.IntakeIncidents.RecordState(inbox, "watching");
            Assert.All(catalog.IntakeIncidents.Recent(20), incident => Assert.NotNull(incident.ResolvedUtc));
            Assert.Equal(start.AddMinutes(2), catalog.IntakeIncidents.Recent(20)[0].ResolvedUtc);
            Assert.Throws<ArgumentOutOfRangeException>(() => catalog.IntakeIncidents.Recent(101));
            Assert.Throws<ArgumentException>(() => catalog.IntakeIncidents.RecordState("relative", "offline"));
            await catalog.Recovery.BackupAsync(Path.Combine(workspace, "backup"), 20_000_000);
        }

        string restored = Path.Combine(workspace, "restored");
        await CatalogSnapshots.RestoreAsync(Path.Combine(workspace, "backup"), restored, 20_000_000);
        using SqliteCatalog reopened = SqliteCatalog.Open(restored, clock);
        Assert.Equal(17, reopened.SchemaVersion);
        Assert.Equal(2, reopened.IntakeIncidents.Recent(20).Count);
        Assert.All(reopened.IntakeIncidents.Recent(20), incident => Assert.NotNull(incident.ResolvedUtc));
    }

    [Fact]
    public void IncidentHistoryIsBounded()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        for (int index = 0; index < 520; index++)
        {
            catalog.IntakeIncidents.RecordState(Path.Combine(workspace, "folder-" + index), "offline");
        }

        Assert.Equal(100, catalog.IntakeIncidents.Recent(100).Count);
        using SqliteConnection check = new($"Data Source={catalog.DatabasePath};Mode=ReadOnly;Pooling=False");
        check.Open();
        using SqliteCommand count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM intake_incidents";
        Assert.Equal(512L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task WatchedRootPublishesOfflineAndRecoveryTransitionsWithoutLeakingSourceData()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        string absent = Path.Combine(workspace, "absent");
        WatchedFolderIntakeGroup group = new(catalog.Sources, catalog.Originals, catalog.DataDirectory,
            [absent], quietPeriod: TimeSpan.Zero, rescanInterval: TimeSpan.FromMilliseconds(100),
            incidents: catalog.IntakeIncidents);
        using CancellationTokenSource cancellation = new();
        Task running = group.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => group.RootStatuses()[0].Status.State == "offline");
            Assert.Equal("offline", Assert.Single(catalog.IntakeIncidents.Recent(20)).State);
            Directory.CreateDirectory(absent);
            await WaitForAsync(() => group.RootStatuses()[0].Status.State == "reapproval_required");
            Assert.Equal("reapproval_required", catalog.IntakeIncidents.Recent(20)[0].State);
            group.ApproveRoot(absent);
            await WaitForAsync(() => group.RootStatuses()[0].Status.State == "watching");
            Assert.All(catalog.IntakeIncidents.Recent(20), incident => Assert.NotNull(incident.ResolvedUtc));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "The intake supervisor did not reach the expected state.");
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
