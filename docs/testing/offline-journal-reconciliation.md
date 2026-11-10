# Offline note reconciliation — #247 / story #18

**Status:** client-side integration under [task #247](https://github.com/berniemackie97/Praxis/issues/247), extracted from [story #18](https://github.com/berniemackie97/Praxis/issues/18) because its required authenticated network/persisted transport [#19](https://github.com/berniemackie97/Praxis/issues/19) and operational #40/#41 gates are not yet implemented. **Do not close #18 or feature #56 from passing these tests.**

## Client boundary and ownership

`OfflineNoteSynchronizer` is a **client-side application coordinator** around the existing durable `LocalNoteJournal` (ADR 0008). `INoteSyncExchange` is an **in-process, application-level, authenticated-and-decrypted projection contract**, deliberately **not** an HTTP DTO, socket protocol, crypto implementation or network serializer. It names the security space, collection epoch, device and exact operation; **UUIDs are not credentials**. The implementing #19 adapter must obtain identity from an actual authenticated TLS connection and enforce principal/device/space/source/current key-epoch authorization at the protected execution boundary from #15.

**Do not send `PendingNoteEdit.Path`, `PendingNoteEdit.Markdown`, or `LocalNoteState` directly over a client-held encrypted network space.** Those contain plaintext. Before remote exchange, the #19 adapter must encrypt/sign record content with independently pinned client keys and verify/decrypt authenticated responses locally, supplying validated projections to this coordinator. Trusted-host-readable spaces require their own expressly authorized processing mode; neither mode may silently downgrade. Local-only collections must not create an `INoteSyncExchange` remote adapter.

A single coordinator serializes reconciliation cycles for one journal with an async gate. The journal still exclusively owns its durable outbox, accepted-operation receipts, current projection, conflicts, resolved contributions and applied cursor. No new durable checkpoint format or parallel sync engine is introduced here.

## Reconnection ordering

1. **Push the durable outbox first.** Up to 128 eligible first-per-record operations are submitted per cycle, retaining their original operation IDs, expected revisions, actions and authored content. This order retries a server commit whose acknowledgement was lost **before** a pull could otherwise misclassify the local pending write as stale. A failed/ambiguous send leaves the journal unchanged; a repeated identical submission is expected to return its original server receipt (ADR 0006).
2. Check response scope (space, epoch, device, operation) before mutating local state. An accepted response must include a positive durable sequence and the exact resulting record projection; `LocalNoteJournal.Accept` validates revision/content and persists the receipt. A stale-revision or path-collision response is preserved via `Reject`; it is **never** silently merged or removed. Wrong epoch yields an explicit `EpochChanged` state. Unauthorized, malformed/version/reused-op responses fail closed without exposing a foreign record.
3. **Pull the exclusive durable cursor.** Up to 32 pages of 1–100 events per cycle are requested from the journal's last **durably applied** cursor, not from a received network acknowledgement. Every page must echo the correct space/epoch/request cursor, ordered final cursor and coherent high-water. The journal validates event sequences/revisions and atomically writes each page checkpoint; an interrupted/malformed page leaves its cursor unchanged.
4. Expired history triggers one verified **same-epoch snapshot rebootstrap**, retaining offline operations and conflict/resolution history. A snapshot cannot erase or roll back any already locally durable record or accepted operation receipt; duplicate or incompatible records fail. A second reset in one cycle becomes `CursorResetRequired` to avoid a loop.
5. Wrong epoch or invalid/future cursor requires a **manual recovery decision**, not automatic clearing of an outbox. No cross-epoch rebase, deletion, blob migration or key rotation is fabricated by this coordinator.

`NoteSyncProgress` reports accepted operations, new visible conflicts, applied changes, whether a snapshot was used, durable cursor, pending operation count and unresolved conflict count. `UpToDate` means the pulled log reached its observed head; it **does not** imply zero unresolved conflicts. `MoreWork` means a bounded cycle stopped before all eligible outbound work/remote changes were processed.

## Fault and acceptance evidence

`OfflineNoteSynchronizerTests` exercises:
- Two **separately persisted local journal directories** edited offline, reconnecting in opposite orders, preserving losing text until deliberate user resolution, converging and reopening from disk
- A committed mutation with **lost acknowledgement** followed by client process restart and stable operation-ID replay without a second server change
- Edit/delete tombstone and rename/path collision, explicit restore or rename resolution, no timestamp-based overwrite or implicit resurrection
- Cursor expiry and same-epoch snapshot retaining local pending/conflicts; incompatible epoch/cursor requiring intervention
- Lost pull response, altered/out-of-order pages and wrong operation receipt, with no partial cursor/outbox commit
- Revoked-device denial with no content/sequence leakage, cancellation, snapshot rollback denial, and a 128-operation/32-page bounded cycle that resumes work

The tests use the existing **in-process `SyncContractFixture`**, which has no database, mTLS, encryption, durable server receipts, network failures or platform-specific operational state. The fault-injection adapter simulates these boundary conditions, **not** their real physical behavior. Client journal state is actually persisted across local restarts; the server fixture is not.

## Remaining #18 / #19 / #20 / #38 gates

- **#19:** real authenticated HTTPS/mTLS ingress and remote persistent source/space state, atomic record+event+receipt commits, bounded requests/paging/rate/connection quotas, crash/lost-ack and replay; compatible ciphertext relay rather than plaintext storage for client-held spaces; actual two-device convergence and revocation tests.
- **#20:** encrypted attachment streaming and verified availability, atomic note/attachment links, deletion/rename reference recovery and historical content retention.
- **#38:** canonical note revision-history/attachment adapter integration, local checkpoint migration, backup and process lifecycle across supported OS implementations.
- **#40/#41/#137:** encrypted backup/restore, operator/CA custody, production deployment and eventually executed cross-platform CI. Owner has deferred blocked CI under #137; a completed GitHub Actions run is not claimed until one actually executes.
- **#18 user-visible story:** interactive conflict/progress display and real device/network reconnect scenarios remain to be verified. The coordinator emits typed progress but is not a finished desktop/mobile UI.

## Local verification for this slice

```sh
git fetch origin feat/issue-247-offline-note-reconciliation
git worktree add --detach ../Praxis-note-sync-247 origin/feat/issue-247-offline-note-reconciliation
cd ../Praxis-note-sync-247
git rev-parse HEAD
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test tests/Praxis.Knowledge.Tests/Praxis.Knowledge.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~OfflineNoteSynchronizerTests|FullyQualifiedName~LocalNoteJournal'
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
python3 -m unittest discover -s scripts/github -p 'test_*.py'
```

Record exact candidate SHA, OS/SDK, exit codes, test totals, formatter result and complete errors. The source-level audit is not a substitute for those results. Correct failures on the **same PR**, re-audit and only then close #247; **do not close #18/#19/#20/#56**.
