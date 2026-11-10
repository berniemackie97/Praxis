using Praxis.Knowledge.Journal;
using Praxis.Knowledge.Synchronization;

namespace Praxis.Knowledge.Tests.Synchronization;

/// <summary>
/// Deterministic in-process protocol fixture. It is not authenticated HTTP,
/// encrypted transport or crash-durable server storage.
/// </summary>
internal sealed class JournalSyncExchangeFixture(Guid securitySpaceId, Guid epoch) : INoteSyncExchange
{
    public SyncContractFixture Server { get; } = new(epoch);
    public HashSet<Guid> RevokedDevices { get; } = [];
    public bool DropNextSubmitAcknowledgment { get; set; }
    public bool DropNextPullResponse { get; set; }
    public Func<NoteSubmitResult, NoteSubmitResult>? AlterNextSubmit { get; set; }
    public Func<NotePullResult, NotePullResult>? AlterNextPull { get; set; }
    public int TotalSubmits { get; private set; }
    public int TotalPulls { get; private set; }

    public Task<NoteSubmitResult> SubmitAsync(NoteSubmitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SecuritySpaceId != securitySpaceId ||
            RevokedDevices.Contains(request.DeviceId))
        {
            return Task.FromResult(new NoteSubmitResult(NoteSubmitStatus.Unauthorized,
                request.SecuritySpaceId, request.CollectionEpoch, request.DeviceId,
                request.Edit.OperationId, 0, null));
        }

        TotalSubmits++;
        SyncMutation mutation = new(request.ProtocolVersion, request.CollectionEpoch,
            request.DeviceId, request.Edit.OperationId, request.Edit.RecordId,
            request.Edit.ExpectedRevision, request.Edit.Action switch
            {
                LocalNoteAction.Upsert => SyncAction.Upsert,
                LocalNoteAction.Delete => SyncAction.Delete,
                LocalNoteAction.Restore => SyncAction.Restore,
                _ => throw new InvalidOperationException()
            }, request.Edit.Path, request.Edit.Markdown, DateTimeOffset.UnixEpoch);
        SyncReply reply = Server.Apply(mutation);
        if (DropNextSubmitAcknowledgment)
        {
            DropNextSubmitAcknowledgment = false;
            throw new IOException("Simulated lost response after the server accepted a mutation.");
        }

        NoteSubmitResult result = new(reply.Status switch
        {
            ApplyStatus.Applied => NoteSubmitStatus.Applied,
            ApplyStatus.StaleRevision => NoteSubmitStatus.StaleRevision,
            ApplyStatus.PathCollision => NoteSubmitStatus.PathCollision,
            ApplyStatus.UnsupportedProtocolVersion => NoteSubmitStatus.UnsupportedProtocolVersion,
            ApplyStatus.WrongCollectionEpoch => NoteSubmitStatus.WrongCollectionEpoch,
            ApplyStatus.InvalidRequest => NoteSubmitStatus.InvalidRequest,
            ApplyStatus.OperationKeyReused => NoteSubmitStatus.OperationKeyReused,
            _ => throw new InvalidDataException()
        }, securitySpaceId, request.CollectionEpoch, request.DeviceId, request.Edit.OperationId,
            reply.Status is ApplyStatus.Applied or ApplyStatus.StaleRevision or ApplyStatus.PathCollision
                ? reply.HeadSequence : 0,
            reply.Current is null ? null : Convert(reply.Current));
        if (AlterNextSubmit is { } alter)
        {
            AlterNextSubmit = null;
            result = alter(result);
        }

        return Task.FromResult(result);
    }

    public Task<NotePullResult> PullAsync(NotePullRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SecuritySpaceId != securitySpaceId ||
            RevokedDevices.Contains(request.DeviceId))
        {
            return Task.FromResult(new NotePullResult(NotePullStatus.Unauthorized,
                request.SecuritySpaceId, request.CollectionEpoch, request.AfterSequence,
                request.AfterSequence, 0, []));
        }

        TotalPulls++;
        SyncPage page = Server.Pull(request.CollectionEpoch, request.AfterSequence, request.Limit);
        if (DropNextPullResponse)
        {
            DropNextPullResponse = false;
            throw new IOException("Simulated response lost before the client committed a page.");
        }

        NotePullResult result = new(page.Status switch
        {
            PullStatus.Ok => NotePullStatus.Ok,
            PullStatus.CursorExpired => NotePullStatus.CursorExpired,
            PullStatus.WrongCollectionEpoch => NotePullStatus.WrongCollectionEpoch,
            PullStatus.InvalidCursor => NotePullStatus.InvalidCursor,
            _ => throw new InvalidDataException()
        }, securitySpaceId, request.CollectionEpoch, request.AfterSequence,
            page.Status == PullStatus.Ok ? page.Cursor : request.AfterSequence,
            page.Status == PullStatus.Ok ? page.HeadSequence : 0,
            page.Changes.Select(c => new AppliedNoteChange(c.Sequence, Convert(c.Record))).ToArray());
        if (AlterNextPull is { } alter)
        {
            AlterNextPull = null;
            result = alter(result);
        }

        return Task.FromResult(result);
    }

    public Task<NoteSnapshotResult> SnapshotAsync(Guid requestedSpace, Guid requestedEpoch,
        Guid deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedSpace != securitySpaceId || requestedEpoch != epoch ||
            RevokedDevices.Contains(deviceId))
        {
            throw new UnauthorizedAccessException("The snapshot is unavailable.");
        }

        SyncSnapshot snapshot = Server.Snapshot();
        return Task.FromResult(new NoteSnapshotResult(securitySpaceId,
            new LocalNoteSnapshot(epoch, snapshot.Cursor, snapshot.Records.Select(Convert).ToArray())));
    }

    private static LocalNoteState Convert(SyncRecord record) =>
        new(record.Id, record.Revision, record.Path!, record.Body!, record.Deleted);
}
