using Praxis.Notes.Journal;
using Xunit;

namespace Praxis.Notes.Tests.Journal;

public sealed class IncomingNoteCollisionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "praxis-incoming-" + Guid.NewGuid().ToString("N"));
    private readonly Guid epoch = Guid.NewGuid();
    private readonly Guid device = Guid.NewGuid();

    [Fact]
    public void IncomingCollisionIsDurableAndExplicitRenameConvergesAfterRestart()
    {
        Guid localId = Guid.NewGuid();
        Guid remoteId = Guid.NewGuid();
        LocalNoteState local = new(localId, 1, "shared.md", "local content", false);
        LocalNoteState remote = new(remoteId, 1, "SHARED.md", "remote content", false);
        using (OfflineNoteJournal journal = Open())
        {
            PendingNoteEdit localEdit = journal.Queue(localId, LocalNoteAction.Upsert,
                local.Path, local.Markdown);
            journal.Accept(localEdit.OperationId, 1, local);
            journal.ApplyPage(0, [new AppliedNoteChange(1, local)]);
            journal.ApplyPage(1, [new AppliedNoteChange(2, remote)]);

            Assert.Equal(2, journal.Cursor);
            Assert.Equal(local, Assert.Single(journal.Records));
            IncomingNoteCollision collision = Assert.Single(journal.IncomingCollisions);
            Assert.Equal(remote, collision.Remote);
            Assert.Equal(localId, collision.BlockingRecordId);
            Assert.Empty(journal.Pending);
            Assert.Throws<InvalidOperationException>(() =>
                journal.Queue(remoteId, LocalNoteAction.Upsert, "other.md", "unsafe"));
            Assert.Throws<InvalidOperationException>(() =>
                journal.ResolveIncoming(remoteId, LocalNoteAction.Upsert, "shared.md"));
            Assert.Equal(2, journal.Cursor);
        }

        PendingNoteEdit rename;
        using (OfflineNoteJournal reopened = Open())
        {
            Assert.Equal(remote, Assert.Single(reopened.IncomingCollisions).Remote);
            rename = reopened.ResolveIncoming(remoteId, LocalNoteAction.Upsert, "other.md");
            Assert.Equal(1, rename.ExpectedRevision);
            Assert.Equal("remote content", rename.Markdown);
            Assert.Equal(rename, Assert.Single(reopened.Outbound()));
            Assert.Single(reopened.IncomingCollisions);
        }

        using (OfflineNoteJournal restarted = Open())
        {
            Assert.Equal(rename, Assert.Single(restarted.Outbound()));
            LocalNoteState accepted = remote with { Revision = 2, Path = "other.md" };
            restarted.Accept(rename.OperationId, 3, accepted);
            Assert.Empty(restarted.IncomingCollisions);
            Assert.Empty(restarted.Pending);
            Assert.Equal(2, restarted.Cursor);
            restarted.ApplyPage(2, [new AppliedNoteChange(3, accepted)]);
            Assert.Equal(3, restarted.Cursor);
            Assert.Empty(restarted.AcknowledgedChanges);
            Assert.Equal(2, restarted.Records.Count);
            Assert.Contains(local, restarted.Records);
            Assert.Contains(accepted, restarted.Records);
        }
    }

    [Fact]
    public void RemoteRevisionUpdatesQuarantineWithoutLosingIncomingContent()
    {
        Guid localId = Guid.NewGuid(), remoteId = Guid.NewGuid();
        using OfflineNoteJournal journal = Open();
        LocalNoteState local = new(localId, 1, "conflict.md", "local", false);
        PendingNoteEdit edit = journal.Queue(localId, LocalNoteAction.Upsert,
            local.Path, local.Markdown);
        journal.Accept(edit.OperationId, 1, local);
        journal.ApplyPage(0, [new AppliedNoteChange(1, local)]);

        LocalNoteState remote = new(remoteId, 1, "conflict.md", "remote-first", false);
        LocalNoteState changed = remote with { Revision = 2, Markdown = "remote-second" };
        journal.ApplyPage(1, [new AppliedNoteChange(2, remote)]);
        journal.ApplyPage(2, [new AppliedNoteChange(3, changed)]);

        Assert.Equal(3, journal.Cursor);
        Assert.Equal(local, Assert.Single(journal.Records));
        Assert.Equal(changed, Assert.Single(journal.IncomingCollisions).Remote);
        PendingNoteEdit resolution = journal.ResolveIncoming(remoteId,
            LocalNoteAction.Upsert, "resolved.md");
        Assert.Equal(2, resolution.ExpectedRevision);
        Assert.Equal("remote-second", resolution.Markdown);
    }

    [Fact]
    public void ExplicitRemoteDeletePreservesExistingLocalPathAndTombstone()
    {
        Guid localId = Guid.NewGuid(), remoteId = Guid.NewGuid();
        using OfflineNoteJournal journal = Open();
        LocalNoteState local = new(localId, 1, "same.md", "local", false);
        PendingNoteEdit edit = journal.Queue(localId, LocalNoteAction.Upsert,
            local.Path, local.Markdown);
        journal.Accept(edit.OperationId, 1, local);
        journal.ApplyPage(0, [new AppliedNoteChange(1, local)]);
        LocalNoteState incoming = new(remoteId, 1, "same.md", "remote", false);
        journal.ApplyPage(1, [new AppliedNoteChange(2, incoming)]);

        PendingNoteEdit deletion = journal.ResolveIncoming(remoteId, LocalNoteAction.Delete);
        Assert.Null(deletion.Path);
        Assert.Null(deletion.Markdown);
        Assert.Equal(1, deletion.ExpectedRevision);
        LocalNoteState tombstone = incoming with { Revision = 2, Deleted = true };
        journal.Accept(deletion.OperationId, 3, tombstone);
        journal.ApplyPage(2, [new AppliedNoteChange(3, tombstone)]);

        Assert.Equal(3, journal.Cursor);
        Assert.Empty(journal.Pending);
        Assert.Empty(journal.IncomingCollisions);
        Assert.Equal(2, journal.Records.Count);
        Assert.Contains(local, journal.Records);
        Assert.Contains(tombstone, journal.Records);
    }

    [Fact]
    public void ExistingOfflineEditCanBeRebasedOntoQuarantinedRemoteRevision()
    {
        Guid localId = Guid.NewGuid(), remoteId = Guid.NewGuid();
        using OfflineNoteJournal journal = Open();
        LocalNoteState local = new(localId, 1, "occupied.md", "local", false);
        PendingNoteEdit initial = journal.Queue(localId, LocalNoteAction.Upsert,
            local.Path, local.Markdown);
        journal.Accept(initial.OperationId, 1, local);
        journal.ApplyPage(0, [new AppliedNoteChange(1, local)]);

        LocalNoteState previousRemote = new(remoteId, 1, "former.md", "remote", false);
        journal.ApplyPage(1, [new AppliedNoteChange(2, previousRemote)]);
        PendingNoteEdit offline = journal.Queue(remoteId, LocalNoteAction.Upsert,
            "offline.md", "private offline contribution");
        LocalNoteState moved = previousRemote with
        {
            Revision = 2,
            Path = "occupied.md",
            Markdown = "remote edited"
        };
        journal.ApplyPage(2, [new AppliedNoteChange(3, moved)]);

        Assert.Equal(3, journal.Cursor);
        Assert.Equal(moved, Assert.Single(journal.IncomingCollisions).Remote);
        RejectedNoteEdit conflict = Assert.Single(journal.Conflicts);
        Assert.Equal(offline.OperationId, conflict.Edit.OperationId);
        Assert.Equal(LocalNoteConflictReason.StaleRevision, conflict.Reason);
        Assert.Equal(moved, conflict.Current);
        PendingNoteEdit resolution = journal.Resolve(offline.OperationId,
            LocalNoteAction.Upsert, "combined.md", "private offline contribution + reviewed remote");
        Assert.Equal(2, resolution.ExpectedRevision);
        Assert.Equal("private offline contribution", Assert.Single(journal.ResolvedConflicts).Edit.Markdown);
        Assert.Empty(journal.Conflicts);
        Assert.Equal(resolution, Assert.Single(journal.Outbound()));
        Assert.Equal(moved, Assert.Single(journal.IncomingCollisions).Remote);

        LocalNoteState accepted = moved with
        {
            Revision = 3,
            Path = "combined.md",
            Markdown = "private offline contribution + reviewed remote"
        };
        journal.Accept(resolution.OperationId, 4, accepted);
        journal.ApplyPage(3, [new AppliedNoteChange(4, accepted)]);
        Assert.Empty(journal.IncomingCollisions);
        Assert.Empty(journal.Pending);
        Assert.Contains(accepted, journal.Records);
        Assert.Equal(4, journal.Cursor);
    }

    [Fact]
    public void MalformedMultiEventCollisionPageCannotPartiallyAdvanceCheckpoint()
    {
        Guid localId = Guid.NewGuid(), remoteId = Guid.NewGuid();
        using OfflineNoteJournal journal = Open();
        LocalNoteState local = new(localId, 1, "same.md", "local", false);
        PendingNoteEdit initial = journal.Queue(localId, LocalNoteAction.Upsert,
            local.Path, local.Markdown);
        journal.Accept(initial.OperationId, 1, local);
        journal.ApplyPage(0, [new AppliedNoteChange(1, local)]);
        LocalNoteState remote = new(remoteId, 1, "same.md", "remote", false);

        Assert.Throws<InvalidDataException>(() => journal.ApplyPage(1,
        [
            new AppliedNoteChange(2, remote),
            new AppliedNoteChange(3, remote with { Revision = 3 })
        ]));
        Assert.Equal(1, journal.Cursor);
        Assert.Empty(journal.IncomingCollisions);
        Assert.Equal(local, Assert.Single(journal.Records));

        journal.ApplyPage(1, [new AppliedNoteChange(2, remote)]);
        Assert.Equal(2, journal.Cursor);
        Assert.Equal(remote, Assert.Single(journal.IncomingCollisions).Remote);
    }

    [Fact]
    public void ResolvingPendingRenamePromotesRetainedIncomingWithoutRewritingRemoteRevision()
    {
        Guid originalId = Guid.NewGuid(), incomingId = Guid.NewGuid();
        using OfflineNoteJournal journal = Open();
        LocalNoteState original = new(originalId, 1, "one.md", "original", false);
        PendingNoteEdit first = journal.Queue(originalId, LocalNoteAction.Upsert,
            original.Path, original.Markdown);
        journal.Accept(first.OperationId, 1, original);
        journal.ApplyPage(0, [new AppliedNoteChange(1, original)]);

        PendingNoteEdit rename = journal.Queue(originalId, LocalNoteAction.Upsert,
            "TWO.md", "renamed original");
        LocalNoteState incoming = new(incomingId, 1, "two.md", "newly arrived", false);
        journal.ApplyPage(1, [new AppliedNoteChange(2, incoming)]);
        Assert.Equal(incoming, Assert.Single(journal.IncomingCollisions).Remote);
        Assert.DoesNotContain(journal.Records, item => item.Id == incomingId);

        journal.Reject(rename.OperationId, LocalNoteConflictReason.PathCollision, original);
        PendingNoteEdit resolution = journal.Resolve(rename.OperationId,
            LocalNoteAction.Upsert, "three.md", "renamed original");
        Assert.Equal(1, resolution.ExpectedRevision);
        Assert.Empty(journal.IncomingCollisions);
        Assert.Contains(incoming, journal.Records);
        Assert.Equal(2, journal.Cursor);

        LocalNoteState renamed = original with
        {
            Revision = 2,
            Path = "three.md",
            Markdown = "renamed original"
        };
        journal.Accept(resolution.OperationId, 3, renamed);
        journal.ApplyPage(2, [new AppliedNoteChange(3, renamed)]);
        Assert.Equal(3, journal.Cursor);
        Assert.Empty(journal.AcknowledgedChanges);
        Assert.Contains(incoming, journal.Records);
        Assert.Contains(renamed, journal.Records);
    }

    [Fact]
    public void AcceptedLocalRenameReleasesRetainedRemoteOnlyAfterDurableAcceptance()
    {
        Guid originalId = Guid.NewGuid(), incomingId = Guid.NewGuid();
        using OfflineNoteJournal journal = Open();
        LocalNoteState original = new(originalId, 1, "shared.md", "local", false);
        PendingNoteEdit first = journal.Queue(originalId, LocalNoteAction.Upsert,
            original.Path, original.Markdown);
        journal.Accept(first.OperationId, 1, original);
        journal.ApplyPage(0, [new AppliedNoteChange(1, original)]);
        LocalNoteState incoming = new(incomingId, 1, "SHARED.md", "remote", false);
        journal.ApplyPage(1, [new AppliedNoteChange(2, incoming)]);

        PendingNoteEdit rename = journal.Queue(originalId, LocalNoteAction.Upsert,
            "renamed.md", "local");
        Assert.Single(journal.IncomingCollisions);
        LocalNoteState accepted = original with { Revision = 2, Path = "renamed.md" };
        journal.Accept(rename.OperationId, 3, accepted);
        Assert.Empty(journal.IncomingCollisions);
        Assert.Contains(incoming, journal.Records);
        Assert.Contains(accepted, journal.Records);
        Assert.Equal(2, journal.Cursor);

        journal.ApplyPage(2, [new AppliedNoteChange(3, accepted)]);
        Assert.Equal(3, journal.Cursor);
        Assert.Empty(journal.AcknowledgedChanges);
    }

    [Fact]
    public void DeletingBlockerReleasesOnlyFirstCandidateAndUpdatesRemainingBlockerAfterRestart()
    {
        Guid originalId = Guid.NewGuid(), firstIncomingId = Guid.NewGuid(), secondIncomingId = Guid.NewGuid();
        using (OfflineNoteJournal journal = Open())
        {
            LocalNoteState original = new(originalId, 1, "same.md", "original", false);
            PendingNoteEdit edit = journal.Queue(originalId, LocalNoteAction.Upsert,
                original.Path, original.Markdown);
            journal.Accept(edit.OperationId, 1, original);
            journal.ApplyPage(0, [new AppliedNoteChange(1, original)]);

            LocalNoteState first = new(firstIncomingId, 1, "same.md", "first", false);
            LocalNoteState second = new(secondIncomingId, 1, "same.md", "second", false);
            journal.ApplyPage(1,
            [
                new AppliedNoteChange(2, first),
                new AppliedNoteChange(3, second)
            ]);
            Assert.Equal(2, journal.IncomingCollisions.Count);
            journal.ApplyPage(3, [new AppliedNoteChange(4, original with
            {
                Revision = 2, Deleted = true
            })]);
            Assert.Empty(journal.Pending);
            Assert.Equal(4, journal.Cursor);
            Assert.Equal(first, Assert.Single(journal.Records, note => !note.Deleted));
            IncomingNoteCollision remaining = Assert.Single(journal.IncomingCollisions);
            Assert.Equal(second, remaining.Remote);
            Assert.Equal(firstIncomingId, remaining.BlockingRecordId);
        }

        using OfflineNoteJournal reopened = Open();
        Assert.Equal(4, reopened.Cursor);
        Assert.Equal(firstIncomingId, Assert.Single(reopened.Records, note => !note.Deleted).Id);
        Assert.Equal(secondIncomingId, Assert.Single(reopened.IncomingCollisions).Remote.Id);
    }

    private OfflineNoteJournal Open() => OfflineNoteJournal.Open(directory, epoch, device);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
