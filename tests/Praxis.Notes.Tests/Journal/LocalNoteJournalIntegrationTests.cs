using Praxis.Notes.Journal;
using Praxis.Notes.Tests.Synchronization;
using Xunit;

namespace Praxis.Notes.Tests.Journal;

public sealed class LocalNoteJournalIntegrationTests : IDisposable
{
    private static readonly Guid Epoch = Guid.Parse("99999999-8888-4777-8666-555555555555");
    private static readonly Guid DeviceA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid DeviceB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid Note = Guid.Parse("66666666-5555-4444-8333-222222222222");
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "praxis-note-client-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TwoClientsPreserveOfflineConflictAndConvergeAfterExplicitMerge()
    {
        SyncContractFixture server = new(Epoch);
        using (OfflineNoteJournal first = OfflineNoteJournal.Open(Path.Combine(directory, "first"), Epoch, DeviceA))
        using (OfflineNoteJournal second = OfflineNoteJournal.Open(Path.Combine(directory, "second"), Epoch, DeviceB))
        {
            PendingNoteEdit created = first.Queue(Note, LocalNoteAction.Upsert, "note.md", "base");
            SyncReply createReceipt = server.Apply(Mutation(DeviceA, created));
            Assert.Equal(ApplyStatus.Applied, createReceipt.Status);
            first.Accept(created.OperationId, createReceipt.HeadSequence, Convert(createReceipt.Current!));

            Pull(server, first);
            Pull(server, second);
            Assert.Equal(first.Records.ToArray(), second.Records.ToArray());

            PendingNoteEdit a = first.Queue(Note, LocalNoteAction.Upsert, "note.md", "first offline");
            PendingNoteEdit b = second.Queue(Note, LocalNoteAction.Upsert, "note.md", "second offline");
            SyncReply accepted = server.Apply(Mutation(DeviceA, a));
            Assert.Equal(ApplyStatus.Applied, accepted.Status);
            first.Accept(a.OperationId, accepted.HeadSequence, Convert(accepted.Current!));

            SyncReply rejected = server.Apply(Mutation(DeviceB, b));
            Assert.Equal(ApplyStatus.StaleRevision, rejected.Status);
            second.Reject(b.OperationId, LocalNoteConflictReason.StaleRevision,
                Convert(rejected.Current!));
            Assert.Equal("second offline", Assert.Single(second.Pending).Markdown);

            Pull(server, second);
            PendingNoteEdit resolved = second.Resolve(b.OperationId, LocalNoteAction.Upsert,
                "note.md", "first offline + second offline");
            Assert.Equal("second offline",
                Assert.Single(second.ResolvedConflicts).Edit.Markdown);
            SyncReply resolutionReceipt = server.Apply(Mutation(DeviceB, resolved));
            Assert.Equal(ApplyStatus.Applied, resolutionReceipt.Status);
            second.Accept(resolved.OperationId, resolutionReceipt.HeadSequence,
                Convert(resolutionReceipt.Current!));

            Pull(server, first);
            Pull(server, second);
            Assert.Equal(3, first.Cursor);
            Assert.Equal(3, second.Cursor);
            Assert.Equal(first.Records.ToArray(), second.Records.ToArray());
            Assert.Equal("first offline + second offline",
                Assert.Single(first.Records).Markdown);
            Assert.Empty(first.Pending);
            Assert.Empty(second.Pending);
        }

        using OfflineNoteJournal reopenedA = OfflineNoteJournal.Open(Path.Combine(directory, "first"), Epoch, DeviceA);
        using OfflineNoteJournal reopenedB = OfflineNoteJournal.Open(Path.Combine(directory, "second"), Epoch, DeviceB);
        Assert.Equal(reopenedA.Records.ToArray(), reopenedB.Records.ToArray());
        Assert.Equal(3, reopenedA.Cursor);
        Assert.Equal(3, reopenedB.Cursor);
        Assert.Single(reopenedB.ResolvedConflicts);
    }

    private static void Pull(SyncContractFixture server, OfflineNoteJournal client)
    {
        SyncPage page = server.Pull(Epoch, client.Cursor, 100);
        Assert.Equal(PullStatus.Ok, page.Status);
        client.ApplyPage(client.Cursor,
            page.Changes.Select(change =>
                new AppliedNoteChange(change.Sequence, Convert(change.Record))).ToArray());
        Assert.Equal(page.Cursor, client.Cursor);
    }

    private static SyncMutation Mutation(Guid device, PendingNoteEdit edit)
        => new(1, Epoch, device, edit.OperationId, edit.RecordId, edit.ExpectedRevision,
            edit.Action switch
            {
                LocalNoteAction.Upsert => SyncAction.Upsert,
                LocalNoteAction.Delete => SyncAction.Delete,
                LocalNoteAction.Restore => SyncAction.Restore,
                _ => throw new InvalidOperationException()
            }, edit.Path, edit.Markdown, DateTimeOffset.UnixEpoch);

    private static LocalNoteState Convert(SyncRecord record)
        => new(record.Id, record.Revision, record.Path!, record.Body!, record.Deleted);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
