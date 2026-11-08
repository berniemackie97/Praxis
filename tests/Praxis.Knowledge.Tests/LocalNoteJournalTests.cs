using System.Text.Json.Nodes;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class LocalNoteJournalTests : IDisposable
{
    private static readonly Guid Epoch = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid DeviceA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid DeviceB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid Note = Guid.Parse("22222222-3333-4444-8555-666666666666");
    private static readonly Guid Other = Guid.Parse("33333333-4444-4555-8666-777777777777");

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "praxis-note-journal-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OfflineEditsSurviveRestartAndAcknowledgementsPreserveSequence()
    {
        Guid firstOperation;
        Guid secondOperation;
        using (var journal = Open())
        {
            var first = journal.Queue(Note, LocalNoteAction.Upsert, "Ideas/first.md", "# First");
            var second = journal.Queue(Note, LocalNoteAction.Upsert, "Ideas/renamed.md", "# Revised");
            firstOperation = first.OperationId;
            secondOperation = second.OperationId;
            Assert.Equal(0, first.ExpectedRevision);
            Assert.Equal(1, second.ExpectedRevision);
            Assert.Equal(first, Assert.Single(journal.Outbound()));
            Assert.Equal(2, journal.Pending.Count);
        }

        using (var reopened = Open())
        {
            Assert.Equal(firstOperation, reopened.Pending[0].OperationId);
            Assert.Equal(secondOperation, reopened.Pending[1].OperationId);
            reopened.Accept(firstOperation, 1, Record(Note, 1, "Ideas/first.md", "# First"));
            Assert.Equal(secondOperation, Assert.Single(reopened.Outbound()).OperationId);
            Assert.Equal(0, reopened.Cursor);
            reopened.Accept(secondOperation, 2, Record(Note, 2, "Ideas/renamed.md", "# Revised"));
            Assert.Empty(reopened.Pending);
            Assert.Equal(2, Assert.Single(reopened.Records).Revision);
        }

        using var restored = Open();
        Assert.Empty(restored.Pending);
        Assert.Equal("Ideas/renamed.md", Assert.Single(restored.Records).Path);
        Assert.Equal(0, restored.Cursor);
    }

    [Fact]
    public void RejectedReceiptCannotDiscardAnUnacknowledgedEdit()
    {
        using var journal = Open();
        var edit = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "original");

        Assert.Throws<InvalidDataException>(() =>
            journal.Accept(edit.OperationId, 1, Record(Note, 1, "note.md", "changed")));
        Assert.Throws<InvalidDataException>(() =>
            journal.Accept(edit.OperationId, 1, Record(Other, 1, "note.md", "original")));
        Assert.Throws<InvalidOperationException>(() =>
            journal.Accept(Guid.NewGuid(), 1, Record(Note, 1, "note.md", "original")));
        Assert.Equal(edit, Assert.Single(journal.Outbound()));
        Assert.Empty(journal.Records);
    }

    [Fact]
    public void DeleteAndRestoreKeepIdentityAndPriorContentRecoverable()
    {
        using var journal = Open();
        var created = journal.Queue(Note, LocalNoteAction.Upsert, "notes/first.md", "# Original");
        journal.Accept(created.OperationId, 1, Record(Note, 1, "notes/first.md", "# Original"));

        var deletion = journal.Queue(Note, LocalNoteAction.Delete);
        Assert.Throws<InvalidOperationException>(() =>
            journal.Queue(Note, LocalNoteAction.Upsert, "notes/first.md", "invalid"));
        journal.Accept(deletion.OperationId, 2, Record(Note, 2, "notes/first.md", "# Original", deleted: true));
        Assert.True(Assert.Single(journal.Records).Deleted);

        var restoration = journal.Queue(Note, LocalNoteAction.Restore, "notes/recovered.md", "# Original");
        journal.Accept(restoration.OperationId, 3, Record(Note, 3, "notes/recovered.md", "# Original"));
        Assert.Equal(Note, Assert.Single(journal.Records).Id);
        Assert.False(Assert.Single(journal.Records).Deleted);
        Assert.Empty(journal.Pending);
    }

    [Fact]
    public void AcknowledgedEditIsVerifiedWhenEchoedByTheChangeLog()
    {
        using var journal = Open();
        var edit = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "accepted");
        var accepted = Record(Note, 1, "note.md", "accepted");
        journal.Accept(edit.OperationId, 1, accepted);

        Assert.Equal(0, journal.Cursor);
        Assert.Equal(1, Assert.Single(journal.AcknowledgedChanges).Sequence);
        Assert.Throws<InvalidDataException>(() => journal.ApplyPage(0, [
            new AppliedNoteChange(1, Record(Note, 1, "note.md", "altered"))
        ]));
        Assert.Equal(0, journal.Cursor);
        Assert.Single(journal.AcknowledgedChanges);

        journal.ApplyPage(0, [new AppliedNoteChange(1, accepted)]);
        Assert.Equal(1, journal.Cursor);
        Assert.Empty(journal.AcknowledgedChanges);
        Assert.Equal(accepted, Assert.Single(journal.Records));
    }

    [Fact]
    public void AcknowledgedReceiptSurvivesRestartUntilMatchingPull()
    {
        using (var journal = Open())
        {
            var edit = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "body");
            journal.Accept(edit.OperationId, 3, Record(Note, 1, "note.md", "body"));
        }

        using var restored = Open();
        Assert.Equal(3, Assert.Single(restored.AcknowledgedChanges).Sequence);
        Assert.Empty(restored.Pending);
        restored.ApplyPage(0, [
            new AppliedNoteChange(1, Record(Other, 1, "other.md", "one")),
            new AppliedNoteChange(2, Record(Other, 2, "other.md", "two")),
            new AppliedNoteChange(3, Record(Note, 1, "note.md", "body"))
        ]);
        Assert.Equal(3, restored.Cursor);
        Assert.Empty(restored.AcknowledgedChanges);
        Assert.Equal(2, restored.Records.Count);
    }

    [Fact]
    public void SameChangeSequenceCannotAcknowledgeDifferentOperations()
    {
        using var journal = Open();
        var first = journal.Queue(Note, LocalNoteAction.Upsert, "one.md", "one");
        var second = journal.Queue(Other, LocalNoteAction.Upsert, "two.md", "two");
        journal.Accept(first.OperationId, 1, Record(Note, 1, "one.md", "one"));

        Assert.Throws<InvalidDataException>(() =>
            journal.Accept(second.OperationId, 1, Record(Other, 1, "two.md", "two")));
        Assert.Equal(second, Assert.Single(journal.Pending));
    }

    [Fact]
    public void RemotePageAtomicallyCommitsCursorAndRecordsAcrossRestart()
    {
        using (var journal = Open())
        {
            journal.ApplyPage(0, [
                new AppliedNoteChange(1, Record(Note, 1, "first.md", "one")),
                new AppliedNoteChange(2, Record(Other, 1, "other.md", "two"))
            ]);
            Assert.Equal(2, journal.Cursor);
            Assert.Equal(2, journal.Records.Count);
        }

        using var restored = Open();
        Assert.Equal(2, restored.Cursor);
        Assert.Equal(2, restored.Records.Count);
        Assert.Throws<InvalidDataException>(() =>
            restored.ApplyPage(0, [new AppliedNoteChange(1, Record(Note, 1, "first.md", "one"))]));
        Assert.Equal(2, restored.Cursor);
    }

    [Fact]
    public void MissingOrOutOfOrderPageFailsWithoutPartialCommit()
    {
        using var journal = Open();
        var initial = new AppliedNoteChange(1, Record(Note, 1, "first.md", "one"));
        var skipped = new AppliedNoteChange(3, Record(Other, 1, "other.md", "two"));
        Assert.Throws<InvalidDataException>(() => journal.ApplyPage(0, [initial, skipped]));
        Assert.Equal(0, journal.Cursor);
        Assert.Empty(journal.Records);

        journal.ApplyPage(0, [initial]);
        Assert.Throws<InvalidDataException>(() => journal.ApplyPage(1, [
            new AppliedNoteChange(2, Record(Note, 4, "first.md", "bad")),
            new AppliedNoteChange(3, Record(Other, 1, "other.md", "two"))
        ]));
        Assert.Equal(1, journal.Cursor);
        Assert.Single(journal.Records);
    }

    [Fact]
    public void RemoteChangesNeverDropOfflineEditsAndExposeConflicts()
    {
        using (var journal = Open())
        {
            journal.ApplyPage(0, [new AppliedNoteChange(1, Record(Note, 1, "note.md", "base"))]);
            var offline = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "offline");
            journal.ApplyPage(1, [new AppliedNoteChange(2, Record(Note, 2, "note.md", "other"))]);

            Assert.Equal(2, journal.Cursor);
            Assert.Empty(journal.Outbound());
            Assert.Equal(offline, Assert.Single(journal.Pending));
            var conflict = Assert.Single(journal.Conflicts);
            Assert.Equal(LocalNoteConflictReason.StaleRevision, conflict.Reason);
            Assert.Equal("offline", conflict.Edit.Markdown);
            Assert.Equal("other", conflict.Current!.Markdown);
        }

        using var restored = Open();
        Assert.Single(restored.Conflicts);
        Assert.Equal("offline", Assert.Single(restored.Pending).Markdown);
    }

    [Fact]
    public void ExplicitConflictResolutionPreservesTheLosingOriginal()
    {
        using var journal = Open();
        journal.ApplyPage(0, [new AppliedNoteChange(1, Record(Note, 1, "note.md", "base"))]);
        var offline = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "my edit");
        journal.ApplyPage(1, [new AppliedNoteChange(2, Record(Note, 2, "note.md", "remote"))]);

        var resolved = journal.Resolve(offline.OperationId, LocalNoteAction.Upsert,
            "note.md", "remote + my edit");
        Assert.NotEqual(offline.OperationId, resolved.OperationId);
        Assert.Equal(2, resolved.ExpectedRevision);
        Assert.Equal("my edit", Assert.Single(journal.ResolvedConflicts).Edit.Markdown);
        Assert.Empty(journal.Conflicts);
        Assert.Equal(resolved, Assert.Single(journal.Outbound()));

        journal.Accept(resolved.OperationId, 3, Record(Note, 3, "note.md", "remote + my edit"));
        Assert.Equal("remote + my edit", Assert.Single(journal.Records).Markdown);
        Assert.Equal(2, journal.Cursor);
        Assert.Empty(journal.Pending);
    }

    [Fact]
    public void DependentOfflineEditsCannotBeSilentlyAbandonedOnConflict()
    {
        using var journal = Open();
        journal.ApplyPage(0, [new AppliedNoteChange(1, Record(Note, 1, "note.md", "base"))]);
        var first = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "first offline");
        var second = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "second offline");
        journal.ApplyPage(1, [new AppliedNoteChange(2, Record(Note, 2, "note.md", "remote"))]);

        Assert.Throws<InvalidOperationException>(() =>
            journal.Resolve(first.OperationId, LocalNoteAction.Upsert, "note.md", "merged"));
        Assert.Equal([first, second], journal.Pending);
        Assert.Single(journal.Conflicts);
        Assert.Empty(journal.Outbound());
    }

    [Fact]
    public void SnapshotResetRetainsPendingEditsAndDetectsNewConflicts()
    {
        using (var journal = Open())
        {
            journal.ApplyPage(0, [new AppliedNoteChange(1, Record(Note, 1, "note.md", "base"))]);
            var pending = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "offline");
            journal.Rebootstrap(new LocalNoteSnapshot(Epoch, 45,
                [Record(Note, 4, "note.md", "remote", deleted: true)]));

            Assert.Equal(45, journal.Cursor);
            Assert.Equal(pending, Assert.Single(journal.Pending));
            Assert.Single(journal.Conflicts);
            Assert.True(Assert.Single(journal.Records).Deleted);
        }

        using var restored = Open();
        Assert.Equal(45, restored.Cursor);
        Assert.Single(restored.Pending);
        Assert.Single(restored.Conflicts);
    }

    [Fact]
    public void ForeignEpochAndInvalidSnapshotCannotReplaceExistingState()
    {
        using var journal = Open();
        var edit = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "offline");

        Assert.Throws<InvalidDataException>(() =>
            journal.Rebootstrap(new LocalNoteSnapshot(Guid.NewGuid(), 20, [])));
        Assert.Throws<InvalidDataException>(() =>
            journal.Rebootstrap(new LocalNoteSnapshot(Epoch, 20, [
                Record(Note, 1, "one.md", "one"),
                Record(Note, 2, "two.md", "two")
            ])));
        Assert.Equal(edit, Assert.Single(journal.Pending));
        Assert.Equal(0, journal.Cursor);
        Assert.Empty(journal.Records);
    }

    [Fact]
    public void ConflictingPathsAndInvalidMetadataAreRejectedBeforeWriting()
    {
        using var journal = Open();
        var edit = journal.Queue(Note, LocalNoteAction.Upsert, "notes/first.md", "first");
        Assert.Throws<InvalidDataException>(() =>
            journal.Queue(Other, LocalNoteAction.Upsert, "../outside.md", "bad"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.Queue(Other, LocalNoteAction.Delete));

        journal.Accept(edit.OperationId, 1, Record(Note, 1, "notes/first.md", "first"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.Queue(Other, LocalNoteAction.Upsert, "NOTES/FIRST.md", "collision"));
        Assert.Single(journal.Records);
        Assert.Empty(journal.Pending);
    }

    [Fact]
    public void PendingCreatesCannotClaimTheSamePath()
    {
        using var journal = Open();
        var first = journal.Queue(Note, LocalNoteAction.Upsert, "same.md", "one");

        Assert.Throws<InvalidOperationException>(() =>
            journal.Queue(Other, LocalNoteAction.Upsert, "SAME.MD", "two"));
        Assert.Equal(first, Assert.Single(journal.Pending));
        Assert.Empty(journal.Records);
    }

    [Fact]
    public void InvalidAcknowledgementAndConflictsAreNotReplayedDifferently()
    {
        using var journal = Open();
        var edit = journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "body");
        journal.Reject(edit.OperationId, LocalNoteConflictReason.PathCollision, null);
        journal.Reject(edit.OperationId, LocalNoteConflictReason.PathCollision, null);
        Assert.Single(journal.Conflicts);
        Assert.Empty(journal.Outbound());

        Assert.Throws<InvalidDataException>(() =>
            journal.Reject(edit.OperationId, LocalNoteConflictReason.StaleRevision, null));
        Assert.Throws<InvalidOperationException>(() =>
            journal.Accept(edit.OperationId, 1, Record(Note, 1, "note.md", "body")));
        Assert.Single(journal.Pending);
    }

    [Fact]
    public void CorruptedCheckpointFailsExplicitlyWithoutResettingOfflineContent()
    {
        using (var journal = Open())
        {
            journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "private note");
        }

        var checkpoint = Path.Combine(directory, "journal.json");
        var envelope = JsonNode.Parse(File.ReadAllText(checkpoint))!.AsObject();
        var payload = envelope["payload"]!.GetValue<string>();
        envelope["payload"] = (payload[0] == 'A' ? 'B' : 'A') + payload[1..];
        var corrupted = envelope.ToJsonString();
        File.WriteAllText(checkpoint, corrupted);

        Assert.Throws<InvalidDataException>(() =>
        {
            using var _ = Open();
        });
        Assert.Equal(corrupted, File.ReadAllText(checkpoint));
    }

    [Fact]
    public void OrphanedTemporaryCheckpointDoesNotOverrideCommittedData()
    {
        using (var journal = Open())
        {
            journal.Queue(Note, LocalNoteAction.Upsert, "note.md", "durable");
        }

        File.WriteAllText(Path.Combine(directory, "journal.json.123456.pending"), "partial");
        using var reopened = Open();
        Assert.Equal("durable", Assert.Single(reopened.Pending).Markdown);
    }

    [Fact]
    public void AJournalCannotBeSilentlyReopenedUnderAnotherIdentity()
    {
        using (var first = Open())
        {
            first.Queue(Note, LocalNoteAction.Upsert, "note.md", "private");
        }

        Assert.Throws<InvalidDataException>(() =>
        {
            using var _ = LocalNoteJournal.Open(directory, Guid.NewGuid(), DeviceA);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            using var _ = LocalNoteJournal.Open(directory, Epoch, DeviceB);
        });

        using var restored = Open();
        Assert.Equal("private", Assert.Single(restored.Pending).Markdown);
    }

    [Fact]
    public void MalformedCheckpointFailsWithoutReinitializingLocalEdits()
    {
        using (var first = Open())
        {
            first.Queue(Note, LocalNoteAction.Upsert, "note.md", "private");
        }

        var checkpoint = Path.Combine(directory, "journal.json");
        File.WriteAllText(checkpoint, "{unparseable");
        Assert.Throws<InvalidDataException>(() =>
        {
            using var _ = Open();
        });
        Assert.Equal("{unparseable", File.ReadAllText(checkpoint));
    }

    [Fact]
    public void JournalIsSingleWriterAndDisposeReleasesItsLease()
    {
        using (var first = Open())
        {
            Assert.Throws<IOException>(() =>
            {
                using var _ = Open();
            });
        }

        using var second = Open();
        var edit = second.Queue(Note, LocalNoteAction.Upsert, "note.md", "body");
        Assert.Equal(edit, Assert.Single(second.Pending));
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => second.Queue(Other,
            LocalNoteAction.Upsert, "other.md", "body"));
    }

    [Fact]
    public void OversizedNoteDoesNotMutateTheCheckpoint()
    {
        using var journal = Open();
        var original = File.ReadAllBytes(Path.Combine(directory, "journal.json"));
        Assert.Throws<InvalidDataException>(() =>
            journal.Queue(Note, LocalNoteAction.Upsert, "note.md", new string('x', 1024 * 1024 + 1)));

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "journal.json")));
        Assert.Empty(journal.Pending);
    }

    [Fact]
    public void ConcurrentQueuesWithinProcessCannotLoseCommittedOperations()
    {
        using (var journal = Open())
        {
            Parallel.For(1, 17, index =>
            {
                var id = Guid.Parse($"12341234-1234-4234-8234-{index:D12}");
                journal.Queue(id, LocalNoteAction.Upsert, $"Notes/{index}.md", $"#{index}");
            });
            Assert.Equal(16, journal.Pending.Count);
            Assert.Equal(16, journal.Outbound().Count);
        }

        using var reopened = Open();
        Assert.Equal(16, reopened.Pending.Count);
        Assert.Equal(16, reopened.Pending.Select(edit => edit.OperationId).Distinct().Count());
    }

    [Fact]
    public void RemotePageCannotApplyMoreThanOneHundredChanges()
    {
        using var journal = Open();
        var page = Enumerable.Range(1, 101)
            .Select(index => new AppliedNoteChange(index,
                Record(Guid.Parse($"78907890-7890-4890-8890-{index:D12}"), 1,
                    $"Notes/{index}.md", $"{index}"))).ToArray();
        Assert.Throws<InvalidDataException>(() => journal.ApplyPage(0, page));
        Assert.Equal(0, journal.Cursor);
    }

    private LocalNoteJournal Open() => LocalNoteJournal.Open(directory, Epoch, DeviceA);

    private static LocalNoteState Record(Guid id, long revision,
        string path, string markdown, bool deleted = false)
        => new(id, revision, path, markdown, deleted);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
