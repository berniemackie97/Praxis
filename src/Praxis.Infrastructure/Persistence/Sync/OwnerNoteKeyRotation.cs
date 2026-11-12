using System.Net;
using System.Security.Cryptography;
using System.Text;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Notes.Journal;

namespace Praxis.Infrastructure.Persistence.Sync;

public static class OwnerNoteKeyRotation
{
    public static async Task<OpaqueSyncRotationReceipt> RotateAsync(
        OfflineNoteJournal journal, Guid spaceId, long previousEpoch,
        long newEpoch, byte[] newContentKey, OpaqueSyncTransportClient transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(newContentKey);
        ArgumentNullException.ThrowIfNull(transport);
        if (spaceId == Guid.Empty || previousEpoch < 1 ||
            previousEpoch == long.MaxValue || newEpoch != previousEpoch + 1 ||
            newContentKey.Length != 32)
        {
            throw new ArgumentException("The key rotation needs a valid space and adjacent key epochs.");
        }

        long head = journal.Cursor;
        LocalNoteState[] records = journal.Records.ToArray();
        if (head < 1 || records.Length is < 1 or > 10000 ||
            journal.Pending.Count != 0 || journal.Conflicts.Count != 0 ||
            journal.IncomingCollisions.Count != 0 ||
            journal.AcknowledgedChanges.Count != 0)
        {
            throw new InvalidOperationException(
                "The owner must fully reconcile the collection and resolve local edits before rotating its key.");
        }

        byte[] key = newContentKey.ToArray();
        try
        {
            byte[] context = Encoding.ASCII.GetBytes(
                $"Praxis.NoteRotation.v1\n{spaceId:N}\n{journal.CollectionEpoch:N}\n{journal.DeviceId:N}\n{newEpoch}\n");
            byte[] identity = HMACSHA256.HashData(key, context);
            Guid rotationId = new(identity.AsSpan(0, 16));
            foreach (LocalNoteState record in records.OrderBy(item => item.Id.ToString("N"),
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                OpaqueSyncAction action = record.Deleted
                    ? OpaqueSyncAction.Delete : OpaqueSyncAction.Upsert;
                byte[] encrypted = ClientOpaqueNoteCipher.Encrypt(key, spaceId,
                    journal.CollectionEpoch, record.Id, record.Revision, action,
                    newEpoch, new OpaqueNoteContent(record.Path, record.Markdown));
                OpaqueSyncRotationStage stage = new(1, spaceId, journal.CollectionEpoch,
                    journal.DeviceId, rotationId, head, previousEpoch, newEpoch,
                    new OpaqueSyncSnapshotRecord(record.Id, record.Revision,
                        record.Deleted, newEpoch, encrypted));
                OpaqueSyncRotationReceipt receipt = await WithRateLimitRetry(
                    () => transport.StageRotationAsync(stage, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                ValidateReceipt(receipt, journal, spaceId, rotationId, newEpoch);
                if (receipt.Status == OpaqueSyncStatus.Applied)
                {
                    return receipt;
                }

                if (receipt.Status != OpaqueSyncStatus.Ok || receipt.HeadSequence != head)
                {
                    throw new InvalidOperationException(
                        "The server refused the encrypted migration stage; the previous projection remains intact.");
                }
            }

            if (journal.Cursor != head || journal.Pending.Count != 0 ||
                !journal.Records.SequenceEqual(records))
            {
                throw new InvalidOperationException(
                    "Local notes changed during key migration; the staged projection was not committed.");
            }

            OpaqueSyncRotationCommit commit = new(1, spaceId, journal.CollectionEpoch,
                journal.DeviceId, rotationId, head, previousEpoch, newEpoch);
            OpaqueSyncRotationReceipt result = await WithRateLimitRetry(
                () => transport.CommitRotationAsync(commit, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            ValidateReceipt(result, journal, spaceId, rotationId, newEpoch);
            if (result.Status != OpaqueSyncStatus.Applied ||
                result.HeadSequence != head)
            {
                throw new InvalidOperationException(
                    "The encrypted key migration was not committed; the previous projection remains available for recovery.");
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidateReceipt(OpaqueSyncRotationReceipt receipt,
        OfflineNoteJournal journal, Guid spaceId, Guid rotationId, long epoch)
    {
        if (receipt is null || receipt.SpaceId != spaceId ||
            receipt.CollectionEpoch != journal.CollectionEpoch ||
            receipt.DeviceId != journal.DeviceId ||
            receipt.RotationId != rotationId || receipt.ContentEpoch != epoch)
        {
            throw new InvalidDataException("The key rotation receipt has a foreign scope.");
        }
    }

    private static async Task<T> WithRateLimitRetry<T>(
        Func<Task<T>> send, CancellationToken cancellationToken)
    {
        for (int retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await send().ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (
                error.StatusCode == HttpStatusCode.TooManyRequests && retry < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
