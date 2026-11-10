using Xunit;

namespace Praxis.Knowledge.Tests.Synchronization;

public sealed class SyncContractTests
{
    private static readonly Guid Collection = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid DeviceA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid DeviceB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid Note = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid OtherNote = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void ConcurrentEditsHaveOneWinnerAndPreserveAnExplicitConflict()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "note.md", "base"));

        SyncMutation alice = Mutation(Note, 1, SyncAction.Upsert, "note.md", "Alice", DeviceA);
        SyncMutation bob = Mutation(Note, 1, SyncAction.Upsert, "note.md", "Bob", DeviceB);
        SyncReply accepted = fixture.Apply(alice);
        SyncReply conflicted = fixture.Apply(bob);

        Assert.Equal(ApplyStatus.Applied, accepted.Status);
        Assert.Equal(ApplyStatus.StaleRevision, conflicted.Status);
        Assert.Equal("Alice", conflicted.Current!.Body);
        Assert.Equal(2, conflicted.Current.Revision);
        Assert.Equal("Bob", bob.Body);
        Assert.Equal(2, fixture.HeadSequence);

        SyncReply resolution = fixture.Apply(Mutation(Note, 2, SyncAction.Upsert,
            "note.md", "Alice and Bob", DeviceB));
        Assert.Equal(ApplyStatus.Applied, resolution.Status);
        Assert.Equal(3, resolution.Current!.Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeleteAndEditRaceNeverSilentlyDropsTheOtherBranch(bool deleteFirst)
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "note.md", "original"));
        SyncMutation deletion = Mutation(Note, 1, SyncAction.Delete, null, null, DeviceA);
        SyncMutation edit = Mutation(Note, 1, SyncAction.Upsert, "note.md", "offline edit", DeviceB);

        SyncReply first = fixture.Apply(deleteFirst ? deletion : edit);
        SyncReply second = fixture.Apply(deleteFirst ? edit : deletion);

        Assert.Equal(ApplyStatus.Applied, first.Status);
        Assert.Equal(ApplyStatus.StaleRevision, second.Status);
        Assert.Equal(2, fixture.HeadSequence);
        Assert.Equal(deleteFirst, fixture.Snapshot().Records.Single().Deleted);
        Assert.Equal(deleteFirst ? "original" : "offline edit",
            fixture.Snapshot().Records.Single().Body);
    }

    [Fact]
    public void DeletedIdentityCannotResurrectWithoutAnExplicitRestore()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "first.md", "original"));
        SyncRecord tombstone = Apply(fixture, Mutation(Note, 1, SyncAction.Delete, null, null));

        Assert.True(tombstone.Deleted);
        Assert.Equal("original", tombstone.Body);
        Assert.Equal(2, tombstone.Revision);
        Assert.Equal(ApplyStatus.StaleRevision,
            fixture.Apply(Mutation(Note, 2, SyncAction.Upsert, "first.md", "hidden")).Status);
        Assert.Equal(ApplyStatus.StaleRevision,
            fixture.Apply(Mutation(Note, 0, SyncAction.Upsert, "first.md", "new identity")).Status);
        Assert.Equal(ApplyStatus.StaleRevision,
            fixture.Apply(Mutation(Note, 1, SyncAction.Restore, "first.md", "old")).Status);

        SyncRecord restored = Apply(fixture, Mutation(Note, 2, SyncAction.Restore, "first.md", "original"));
        Assert.Equal(Note, restored.Id);
        Assert.Equal(3, restored.Revision);
        Assert.False(restored.Deleted);
    }

    [Fact]
    public void TombstoneReleasesPathButRestoringCannotStealAnOccupiedPath()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "first.md", "one"));
        Apply(fixture, Mutation(Note, 1, SyncAction.Delete, null, null));
        Apply(fixture, Mutation(OtherNote, 0, SyncAction.Upsert, "FIRST.md", "two"));

        Assert.Equal(ApplyStatus.PathCollision,
            fixture.Apply(Mutation(Note, 2, SyncAction.Restore, "first.md", "one")).Status);
        SyncRecord recovered = Apply(fixture, Mutation(Note, 2, SyncAction.Restore, "recovered.md", "one"));
        Assert.Equal("recovered.md", recovered.Path);
        Assert.Equal(4, fixture.HeadSequence);
    }

    [Fact]
    public void SameOperationRetriesDoNotAppendTwiceEvenAfterLaterChanges()
    {
        SyncContractFixture fixture = new(Collection);
        SyncMutation original = Mutation(Note, 0, SyncAction.Upsert, "note.md", "one");
        SyncReply receipt = fixture.Apply(original);
        Apply(fixture, Mutation(Note, 1, SyncAction.Upsert, "note.md", "two"));

        Assert.Equal(receipt, fixture.Apply(original));
        Assert.Equal(2, fixture.HeadSequence);
        Assert.Equal(ApplyStatus.OperationKeyReused,
            fixture.Apply(original with { Body = "different" }).Status);
        Assert.Equal(2, fixture.HeadSequence);

        fixture.PruneThrough(1);
        Assert.Equal(receipt, fixture.Apply(original));
        Assert.Equal(2, fixture.HeadSequence);
    }

    [Fact]
    public void OperationKeysAreScopedToTheirAuthenticatedDeviceIdentity()
    {
        SyncContractFixture fixture = new(Collection);
        Guid operation = Guid.NewGuid();
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "one.md", "one",
            DeviceA, operation));
        Apply(fixture, Mutation(OtherNote, 0, SyncAction.Upsert, "two.md", "two",
            DeviceB, operation));

        Assert.Equal(2, fixture.HeadSequence);
    }

    [Fact]
    public void ClientClockSkewDoesNotChangeAcceptedRevisionOrdering()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "note.md", "first",
            DeviceA, time: DateTimeOffset.MaxValue));
        SyncRecord later = Apply(fixture, Mutation(Note, 1, SyncAction.Upsert, "note.md", "second",
            DeviceB, time: DateTimeOffset.MinValue));

        Assert.Equal("second", later.Body);
        Assert.Equal(2, later.Revision);
        Assert.Equal(2, fixture.HeadSequence);
    }

    [Fact]
    public void RenameAndConcurrentContentEditRequireExplicitReconciliation()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "old.md", "content"));
        Apply(fixture, Mutation(Note, 1, SyncAction.Upsert, "new.md", "content", DeviceA));

        SyncReply conflict = fixture.Apply(Mutation(Note, 1, SyncAction.Upsert,
            "old.md", "new text", DeviceB));
        Assert.Equal(ApplyStatus.StaleRevision, conflict.Status);
        Assert.Equal("new.md", conflict.Current!.Path);

        Apply(fixture, Mutation(Note, 2, SyncAction.Upsert, "new.md", "new text", DeviceB));
        Assert.Equal(3, fixture.HeadSequence);
        Assert.Equal(Note, fixture.Snapshot().Records.Single().Id);
    }

    [Fact]
    public void ActivePathsConflictCaseInsensitivelyAcrossRecordIdentities()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "Assets/one.md", "first"));

        SyncReply collision = fixture.Apply(Mutation(OtherNote, 0, SyncAction.Upsert,
            "assets/ONE.md", "second"));
        Assert.Equal(ApplyStatus.PathCollision, collision.Status);
        Assert.Equal(1, fixture.HeadSequence);
        Assert.Single(fixture.Snapshot().Records);
    }

    [Fact]
    public void PullUsesExclusiveDurableCursorAndStablePages()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "note.md", "v1"));
        Apply(fixture, Mutation(Note, 1, SyncAction.Upsert, "note.md", "v2"));
        Apply(fixture, Mutation(Note, 2, SyncAction.Delete, null, null));

        SyncPage first = fixture.Pull(Collection, 0, 2);
        Assert.Equal(PullStatus.Ok, first.Status);
        Assert.Equal([1L, 2L], first.Changes.Select(change => change.Sequence));
        Assert.Equal(2, first.Cursor);
        Assert.Equal(3, first.HeadSequence);

        SyncPage second = fixture.Pull(Collection, first.Cursor, 2);
        Assert.Equal([3L], second.Changes.Select(change => change.Sequence));
        Assert.True(Assert.Single(second.Changes).Record.Deleted);
        Assert.Equal(3, second.Cursor);
        Assert.Empty(fixture.Pull(Collection, second.Cursor, 2).Changes);
        Assert.Equal(3, fixture.HeadSequence);
    }

    [Fact]
    public void ExpiredCursorRequiresSnapshotIncludingTombstones()
    {
        SyncContractFixture fixture = new(Collection);
        Apply(fixture, Mutation(Note, 0, SyncAction.Upsert, "note.md", "original"));
        Apply(fixture, Mutation(Note, 1, SyncAction.Delete, null, null));
        fixture.PruneThrough(1);

        Assert.Equal(PullStatus.CursorExpired, fixture.Pull(Collection, 0, 5).Status);
        SyncSnapshot snapshot = fixture.Snapshot();
        Assert.Equal(2, snapshot.Cursor);
        Assert.True(Assert.Single(snapshot.Records).Deleted);
        Assert.Equal("original", snapshot.Records[0].Body);

        SyncPage replay = fixture.Pull(Collection, 1, 5);
        Assert.Equal(PullStatus.Ok, replay.Status);
        Assert.Single(replay.Changes);
    }

    [Fact]
    public void EpochAndFutureCursorAreRejectedWithoutChangingState()
    {
        SyncContractFixture fixture = new(Collection);
        Guid otherEpoch = Guid.NewGuid();

        Assert.Equal(PullStatus.WrongCollectionEpoch,
            fixture.Pull(otherEpoch, 0, 10).Status);
        Assert.Equal(PullStatus.InvalidCursor,
            fixture.Pull(Collection, 1, 10).Status);
        Assert.Equal(PullStatus.InvalidCursor,
            fixture.Pull(Collection, -1, 10).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Pull(Collection, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Pull(Collection, 0, 101));
        Assert.Equal(0, fixture.HeadSequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedProtocolVersionsAreRejectedBeforeAnyWrite(int version)
    {
        SyncContractFixture fixture = new(Collection);
        SyncMutation attempted = Mutation(Note, 0, SyncAction.Upsert, "note.md", "payload",
            version: version);

        Assert.Equal(ApplyStatus.UnsupportedProtocolVersion, fixture.Apply(attempted).Status);
        Assert.Empty(fixture.Snapshot().Records);
        Assert.Equal(0, fixture.HeadSequence);
    }

    [Fact]
    public void WrongCollectionEpochDoesNotConsumeAnOperationKey()
    {
        SyncContractFixture fixture = new(Collection);
        SyncMutation mutation = Mutation(Note, 0, SyncAction.Upsert, "note.md", "payload");
        Assert.Equal(ApplyStatus.WrongCollectionEpoch,
            fixture.Apply(mutation with { CollectionEpoch = Guid.NewGuid() }).Status);

        Assert.Equal(ApplyStatus.Applied, fixture.Apply(mutation).Status);
        Assert.Equal(1, fixture.HeadSequence);
    }

    [Fact]
    public void IndependentOfflineCreatesConvergeDespiteDeliveryReordering()
    {
        SyncMutation first = Mutation(Note, 0, SyncAction.Upsert, "one.md", "one", DeviceA);
        SyncMutation second = Mutation(OtherNote, 0, SyncAction.Upsert, "two.md", "two", DeviceB);
        SyncContractFixture a = new(Collection);
        SyncContractFixture b = new(Collection);

        Apply(a, first);
        Apply(a, second);
        Apply(b, second);
        Apply(b, first);

        Assert.Equal(a.Snapshot().Records, b.Snapshot().Records);
        Assert.Equal(2, a.HeadSequence);
        Assert.Equal(2, b.HeadSequence);
    }

    [Fact]
    public void IncompatibleEpochMigrationRejectsOldCursorsAndPendingOperations()
    {
        SyncContractFixture original = new(Collection);
        SyncMutation pending = Mutation(Note, 0, SyncAction.Upsert, "note.md", "offline");
        Apply(original, pending);
        long oldCursor = original.HeadSequence;

        Guid newEpoch = Guid.Parse("44444444-4444-4444-8444-444444444444");
        SyncContractFixture restored = new(newEpoch);

        Assert.Equal(PullStatus.WrongCollectionEpoch,
            restored.Pull(Collection, oldCursor, 10).Status);
        Assert.Equal(ApplyStatus.WrongCollectionEpoch, restored.Apply(pending).Status);
        Assert.Empty(restored.Snapshot().Records);
        Assert.Equal(0, restored.HeadSequence);
        Assert.NotEqual(original.CollectionEpoch, restored.CollectionEpoch);
    }

    [Fact]
    public void InvalidRequestsAndRejectedWritesNeverChangeSequence()
    {
        SyncContractFixture fixture = new(Collection);
        Assert.Equal(ApplyStatus.InvalidRequest, fixture.Apply(
            Mutation(Note, -1, SyncAction.Upsert, "note.md", "body")).Status);
        Assert.Equal(ApplyStatus.InvalidRequest, fixture.Apply(
            Mutation(Note, 0, SyncAction.Delete, "note.md", null)).Status);
        Assert.Equal(ApplyStatus.StaleRevision, fixture.Apply(
            Mutation(Note, 0, SyncAction.Delete, null, null)).Status);
        Assert.Equal(ApplyStatus.InvalidRequest, fixture.Apply(
            Mutation(Note, 0, SyncAction.Upsert, null, "body")).Status);
        Assert.Equal(0, fixture.HeadSequence);
        Assert.Empty(fixture.Snapshot().Records);
    }

    private static SyncMutation Mutation(
        Guid recordId, long baseRevision, SyncAction action, string? path, string? body,
        Guid? device = null, Guid? operationId = null, DateTimeOffset? time = null, int version = 1)
        => new(version, Collection, device ?? DeviceA, operationId ?? Guid.NewGuid(),
            recordId, baseRevision, action, path, body, time ?? DateTimeOffset.UnixEpoch);

    private static SyncRecord Apply(SyncContractFixture fixture, SyncMutation mutation)
    {
        SyncReply reply = fixture.Apply(mutation);
        Assert.Equal(ApplyStatus.Applied, reply.Status);
        return Assert.IsType<SyncRecord>(reply.Current);
    }
}
