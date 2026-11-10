using Praxis.Knowledge.Journal;

namespace Praxis.Knowledge.Synchronization;

public sealed partial class OfflineNoteSynchronizer
{
    private async Task<(int Accepted, NoteSyncState? EarlyState)> SubmitPendingAsync(
        Guid epoch, Guid deviceId, CancellationToken cancellationToken)
    {
        int accepted = 0;
        // One cycle is bounded even when the journal has a large offline outbox.
        for (int sent = 0; sent < MaximumOperationsPerCycle; sent++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingNoteEdit? edit = journal.Outbound().FirstOrDefault();
            if (edit is null)
            {
                break;
            }

            NoteSubmitRequest request = new(ProtocolVersion, securitySpaceId, epoch,
                deviceId, edit);
            NoteSubmitResult response = await exchange.SubmitAsync(request, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (response is null || response.ProtocolVersion != ProtocolVersion ||
                response.SecuritySpaceId != securitySpaceId ||
                response.DeviceId != deviceId || response.OperationId != edit.OperationId)
            {
                throw new InvalidDataException("The receipt does not match the submitted operation.");
            }

            if (response.Status == NoteSubmitStatus.WrongCollectionEpoch)
            {
                RequireNoPrivateResponse(response);
                return (accepted, NoteSyncState.EpochChanged);
            }

            if (response.CollectionEpoch != epoch)
            {
                throw new InvalidDataException("The receipt uses a foreign collection epoch.");
            }

            switch (response.Status)
            {
                case NoteSubmitStatus.Applied:
                    if (response.Sequence <= 0 || response.Current is null)
                    {
                        throw new InvalidDataException("An accepted operation requires a durable receipt.");
                    }

                    journal.Accept(edit.OperationId, response.Sequence, response.Current);
                    accepted++;
                    break;
                case NoteSubmitStatus.StaleRevision:
                case NoteSubmitStatus.PathCollision:
                    if (response.Current is not null && response.Current.Id != edit.RecordId)
                    {
                        throw new InvalidDataException("A conflict cannot disclose a foreign record.");
                    }

                    journal.Reject(edit.OperationId,
                        response.Status == NoteSubmitStatus.StaleRevision
                            ? LocalNoteConflictReason.StaleRevision
                            : LocalNoteConflictReason.PathCollision,
                        response.Current);
                    break;
                case NoteSubmitStatus.Unauthorized:
                    RequireNoPrivateResponse(response);
                    throw new UnauthorizedAccessException(
                        "The device is not authorized to synchronize this space.");
                case NoteSubmitStatus.UnsupportedProtocolVersion:
                    RequireNoPrivateResponse(response);
                    throw new NotSupportedException("The note protocol version is unsupported.");
                case NoteSubmitStatus.InvalidRequest:
                case NoteSubmitStatus.OperationKeyReused:
                    RequireNoPrivateResponse(response);
                    throw new InvalidDataException("The endpoint rejected the operation contract.");
                default:
                    throw new InvalidDataException("The endpoint returned an unknown operation result.");
            }
        }

        return (accepted, null);
    }

    private static void RequireNoPrivateResponse(NoteSubmitResult response)
    {
        if (response.Current is not null || response.Sequence != 0)
        {
            throw new InvalidDataException("A rejected operation disclosed private state.");
        }
    }
}
