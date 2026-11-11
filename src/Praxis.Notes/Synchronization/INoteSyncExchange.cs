namespace Praxis.Notes.Synchronization;

/// <summary>
/// Client-side projections after an authenticated and decrypted exchange. These
/// contracts are not an HTTP wire format. A remote adapter must enforce device,
/// security-space and key-epoch authorization and never transmit plaintext for
/// client-held encrypted spaces.
/// </summary>
public interface INoteSyncExchange
{
    Task<NoteSubmitResult> SubmitAsync(NoteSubmitRequest request, CancellationToken cancellationToken);
    Task<NotePullResult> PullAsync(NotePullRequest request, CancellationToken cancellationToken);
    Task<NoteSnapshotResult> SnapshotAsync(Guid securitySpaceId, Guid collectionEpoch, Guid deviceId,
        CancellationToken cancellationToken);
}
