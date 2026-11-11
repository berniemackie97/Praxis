# Authenticated offline note journal integration (#18)

This integration connects the existing owner-local `OfflineNoteJournal` and `OfflineNoteSynchronizer` to the separate opt-in private sync host. The journal remains authoritative for unsent edits, durable cursors and unresolved conflicts; an `AuthenticatedNoteExchange` supplies the client-decrypted `INoteSyncExchange` contract.

## Security and durability boundary

- A caller supplies an already enrolled, pinned mTLS `OpaqueSyncTransportClient`, the journal, the authorized security-space ID, current content-key epoch, 32-byte client-held key and an **existing, dedicated, owner-private outbox directory**. The adapter never acquires the key from the host. It disposes its private key copy; callers dispose their transport and journal independently.
- Each pending operation is sealed once with AES-256-GCM (associated data binds space, collection epoch, record, revision, action and key epoch). The exact ciphertext and operation parameters are committed as a private file **before any network submit**. A lost HTTP response or process restart replays these bytes, not a new random nonce. A mismatched device/collection/key epoch, changed operation or unreadable sealed file is a hard error, not a silent re-encryption.
- The outbox belongs to exactly one device/space/collection/key epoch. An uninitialized directory must be empty. Pruning removes only recognised GUID-named entries absent from the durable journal outbox; an unrelated JSON file is never treated as disposable content. The outbox and all entries must be owner-private on Unix, and symlinked roots/entries are refused.
- Accepted network receipts only become acknowledged when `OfflineNoteJournal.Accept` finishes its durable checkpoint. Pull pages are fully decrypted and checked before a single journal page checkpoint advances the cursor. The local journal preserves pending contributions when transport, cryptography or persistence fails.
- Encrypted deletions carry the prior path/text **inside** the authenticated ciphertext; their remote event retains a `Deleted` flag and the sealed bytes. This is compatible with the server's previously allowed empty-ciphertext tombstones but the new client **refuses** such legacy, unauthenticated deletions instead of trusting an untrusted relay to erase local content. No plaintext note body or path is transmitted to the server.
- A stale edit is never overwritten by a server timestamp. The client first retains its losing edit as a conflict; after authenticating the intervening events it refreshes the conflict's current record, allowing an explicit user resolution. The host cannot see a client's decrypted note paths and therefore **cannot** decide plaintext path collisions.

The local checkpoint and sidecar do not constitute an encrypted complete backup, nor a guarantee of zero data loss during sudden power failure. They require owner-managed durable storage, device/key recovery and backups. No production host TLS policy, public network listener, or security-space grant is changed by this integration.

## Acceptance coverage and still-open contract

The merged host test fixture is the real HTTPS client-certificate + one-time proof path, with a disposable pinned CA and independent persisted client journal directories. New tests exercise bidirectional convergence, stale offline edit recovery, sealed deletion and restoration, lost receipt followed by host and client restart, changed-key denial, and rejection of legacy unsealed deletion. Unit tests cover byte-stable replay, private permissions, directory binding, foreign epoch/device and non-destructive pruning.

The following remain **unsatisfied** and must not be checked off by the passing tests above:

- **Atomic path-collision recovery:** a client-held ciphertext server cannot inspect note paths. The journal currently rejects a pulled page that would create duplicate active paths without advancing the cursor; it does not yet expose a durable, user-resolvable incoming-path collision. That requires an explicit client-side reconciliation design, not weakening catalog path invariants or making the server decrypt.
- **Consistent encrypted snapshot/rebootstrap:** the host can signal stale/invalid cursor and key reset, but has no authenticated consistent, bounded snapshot endpoint. `SnapshotAsync` deliberately fails closed and leaves outbox/cursor intact. Rotation and pruned-history recovery must be added and independently tested before #18 completion.
- **Remote attachment transfer and atomic note references:** owned by #20, not provided by note-only ciphertext events.
- **Independent real devices and operational release:** genuine network interruption, revocation and epoch rollover on owner-approved hardware, reliable CA/key escrow policy, supported-host TLS 1.3, and current-head Linux/Windows/macOS verification under #65/#137. macOS test-only loopback TLS 1.2 does **not** certify a production macOS sync host.

The next cohesive work should implement encrypted bounded host snapshots and client-side name-collision reconciliation in the same journal contract; do not introduce parallel note storage or an unsafe raw-plaintext HTTP adapter.

## Reproducible verification

From a clean checkout of the PR's exact head, with .NET 10:

```sh
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test tests/Praxis.Notes.Tests/Praxis.Notes.Tests.csproj -c Release --no-build --no-restore &&
dotnet test tests/Praxis.Infrastructure.Tests/Praxis.Infrastructure.Tests.csproj -c Release --no-build --no-restore &&
dotnet test tests/Praxis.SyncHost.Tests/Praxis.SyncHost.Tests.csproj -c Release --no-build --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --verify-no-changes --no-restore &&
python3 -m unittest discover -s scripts/github -p 'test_*.py' -v &&
git diff --check
```

The macOS SyncHost tests are **loopback-only TLS 1.2** through the existing internal fixture because .NET 10 Kestrel does not provide production TLS 1.3 on macOS. The public sync client and production host keep TLS 1.3; test Linux/Windows sockets independently. Never claim green CI when Actions jobs have not executed (#137).

Use synthetic note bodies, synthetic keys and test CA fixtures. Do not commit private recovery material or production host credentials.
