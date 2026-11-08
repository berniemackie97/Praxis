# Adversarial synchronization contract tests

Issue #21 exercises the accepted [synchronization decision](../decisions/0006-versioned-sync-protocol.md) using an in-memory protocol fixture and simulated offline client checkpoints. These tests execute in the existing .NET knowledge test project; they neither start a service nor use user data or credentials.

## Deterministic scenarios

- Seeded delivery permutations across three independent offline devices, including duplicate submissions, lost replies, bounded pull pages, dropped responses and repeated page delivery. Successful records must converge to the same identities, versions, content and cursor.
- Seeded concurrent rename, edit and delete races. The server accepts one mutation against the shared revision; other contributions remain available as explicit conflicts. A new intentional resolution operation allows replicas to converge without silent last-write-wins behavior.
- An accepted server mutation with lost acknowledgment, followed by client restart and identical operation replay, must not advance the authoritative change sequence again.
- Lost and interrupted pull responses cannot advance a simulated client's applied cursor. Duplicate pages are harmless; missing or reordered events fail without partially changing the local projection.
- An expired cursor requires a full snapshot containing tombstones, while pending offline changes remain queued and receive an explicit conflict if they are stale.
- A different collection epoch rejects old pulls and snapshots without consuming or silently discarding pending edits. Invalid acknowledgments and impossible empty-page cursors also fail.

The scenario inputs are fixed seeds and explicit operations. Failures in seeded delivery report the seed and client index. No wall-clock time, random global state, network latency or external dependencies affect expectations.

## What these tests prove

They demonstrate the chosen *contract* against deterministic failure schedules: no accepted mutation is silently overwritten, retries do not create duplicate events, page application is all-or-nothing in the simulated projection, and divergent client states converge after an explicit resolution.

The OfflineSyncReplica.Checkpoint method models a hypothetical durable client checkpoint by copying the local state. Restart restores from that copy. This **does not prove** a filesystem/database transaction, crash consistency, thread safety, process isolation, network authentication, disk durability, key recovery or real device behavior.

The authoritative SyncContractFixture is also in-memory and sequential. It does not implement atomic database commits or authenticated device identities. A real system must verify these properties with persistence/transport integrations and fault injection before remote synchronization can be enabled.

## Future integration gates

- #15 and #50: device identity, authorization, privacy mode and recovery custody.
- #18: durable client outbox, server revision/receipt transaction and offline resume.
- #19: bounded authenticated requests, interrupted transfers, rate controls and observable error codes.
- #20: staging and atomic reference commits for binary attachments.
- #40 and #47: backup recovery, corruption/power-loss testing and measured resilience.

Do not reuse the test-only replica as a production storage engine. It deliberately lacks a transaction log, thread synchronization and security boundaries.
