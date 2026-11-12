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

The following remain **unsatisfied** and must not be checked off until verified:

- **Incoming path collisions:** when a decrypted remote note conflicts with an active or pending local path, the journal checkpoints its authenticated content in a separate incoming-collision projection, advances the durable event cursor, and reports `ConflictsPending`. The active projection keeps unique paths. An explicit local conflict rebase, accepted rename or remote deletion can release a path: the journal promotes the earliest unblocked retained record in the same durable checkpoint without issuing a new remote write, while other claimants remain quarantined with the current blocker identity. It does not promote a record while it has a pending local edit. If the path is still occupied, `ResolveIncoming` requires an explicit new path or deletion and creates a revision-checked durable outbound operation. Retries, stale revisions and restarts retain unresolved remote content. This behavior must pass owner-executed tests before acceptance.
- **Consistent encrypted snapshot/rebootstrap:** the private mTLS host now offers a signed-proof, read-only, per-record encrypted snapshot paged at a fixed committed log head. The client authenticates every payload under the client-held content key, validates record identity/order/count, and replaces the local projection in one journal checkpoint only after all pages arrive. If a writer changes the head between pages, the store refuses further pages and the client retains its original checkpoint and outbox. Host maintenance may prune history only after a committed record projection remains. Test expiry, page completion, key mismatch, corrupted ciphertext and pending conflict preservation on the exact PR head before accepting this behavior. This does not perform content-key epoch rotation.
- **Remote attachment transfer and atomic note references:** owned by #20, not provided by note-only ciphertext events.
- **Independent real devices and operational release:** genuine network interruption, revocation and epoch rollover on owner-approved hardware, reliable CA/key escrow policy, supported-host TLS 1.3, and current-head Linux/Windows/macOS verification under #65/#137. macOS test-only loopback TLS 1.2 does **not** certify a production macOS sync host.

The remaining recovery boundary is **content-key epoch migration**: the owner must recover the previous key and its complete current projection, prepare ciphertext authenticated under the new epoch, and commit a full replacement with transactionally fenced revisions before newly enrolled clients can bootstrap. The current host refuses mixed old/new epochs and leaves old ciphertext intact; it does **not** silently rewrite key epochs, reset cursors, or discard outstanding edits. A locally authenticated snapshot is not a collection-wide signed completeness proof against a malicious host that deliberately hides unknown records. Do not introduce parallel plaintext storage or an unsafe raw-plaintext HTTP adapter.

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
