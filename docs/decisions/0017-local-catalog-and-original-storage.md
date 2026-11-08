# ADR 0017: Embedded owner-local catalog and original objects

**Status:** Proposed in #99 / PR #121; becomes the reviewed **local-only** implementation direction on merge. No runtime database has been added.

## Problem and existing evidence

Praxis must accept user-selected files, mixed media and future voice/browser/agent submissions without Obsidian. The existing archive exchange ([ADR 0004](0004-portable-note-archive.md), [ADR 0005](0005-attachment-collection-archive.md), [ADR 0009](0009-local-vault-capture-restore.md)) is correct for bounded portable snapshots, but its **256 MiB total collection and 32 MiB per-attachment limits**, and materialization of binaries in memory, cannot serve as the operational catalog for multi-GiB video or an expanding mixed-media library.

The existing [staged attachment transfer](0007-local-attachment-staging.md) proves useful content hashing, verified publication and restart-safe offset handling under **32 MiB**, but does not supply source/item identity, catalog transaction coordination, deletion/revocation, shared-object reachability or recovery. Reuse its demonstrated publication principles, not its transfer-size limits or unrelated transfer protocol.

## Recommended local-only decision

Use **SQLite as the embedded transactional catalog**, with versioned schema, normal foreign-key checks, WAL journal and `synchronous=FULL` on a verified **local filesystem**. Store original content as independently verified, content-addressed **regular files**, not SQLite BLOBs. Run one explicitly owner-started local process with a bounded ingestion worker; no database daemon, hosted service or Obsidian dependency.

This is a design choice for the first local owner-only profile, **not** an approval of a particular .NET SQLite provider package, the #50 trust mode, encryption, remote sharing, mobile DB synchronization, or an API security mechanism. A provider/runtime integration and installation test belongs to #100, and a loopback caller authorization decision belongs to #107.

### Alternatives considered

| Candidate | Benefits | Main cost or failure case | Decision |
| --- | --- | --- | --- |
| SQLite catalog + objects directory | Small embedded engine; transactional multi-table constraints, durable commits, indexed lookups, standard backup/migration mechanisms, no daemon | Two-phase coordination across catalog and filesystem; WAL/local-filesystem and backup discipline; provider must be selected/tested | **Selected for #100** |
| Append-only JSONL + object files | Human-inspectable records, simple append, no database dependency | Replay/compaction and cross-record referential integrity, partial-line recovery, indexing and concurrent update policy must be built and audited | Reject as authoritative catalog; may suit diagnostic/event exports |
| Snapshot JSON manifest + immutable objects | Straightforward complete export and restore | Whole-catalog rewrite and atomic replacement per update, cross-file transaction/recovery and query indexes are self-maintained | Reject as live catalog; use explicit portable exports |
| Original bytes in SQLite BLOBs | One transaction can commit metadata and payload bytes together | Large original payloads amplify DB/WAL growth, backup, compaction and memory/I/O pressure; limits independent media processing | Reject for multi-GiB originals |
| Network SQL service / document store | Multi-host concurrency and centralized access | Requires deployment, secrets, network/tenant trust and recovery decisions explicitly pending #50 | Not needed for the first local application |

SQLite's [WAL documentation](https://www.sqlite.org/wal.html) restricts its shared-memory coordination to clients on the same computer and warns against network filesystems. Its [synchronous modes](https://www.sqlite.org/pragma.html#pragma_synchronous) distinguish `FULL` WAL commit durability from `NORMAL` WAL's potential loss of recent commits on power failure. The chosen settings are conservative; they do **not** guarantee that an external drive, OS flush implementation or sudden unplug preserves every committed cross-file state. Test those limits under #100/#40.

## Logical data contract

The research-only [v1 schema fixture](../../scripts/research/local-catalog.schema.sql) captures the first minimum:

- `sources`: stable UUID (the fixture uses opaque text IDs), input kind, canonical source locator, first capture time and active/revoked state. A path/URL is an **attribute**, not an item ID. A folder import is represented by its member sources with their own identities. Manual recordings/notes use generated locators; re-capturing an existing source creates a new version or idempotent receipt, not another logical source.
- `items`: separate stable item UUID tied to a source in the first release. Distinct source identities remain distinct even if bytes are equal; later explicit cross-source merging must not discard provenance.
- `original_objects`: lowercase SHA-256 digest and original byte count, checked independently against a content-addressed object on disk. The digest indicates integrity, **not authenticity or permission**.
- `item_versions`: increasing positive revision per item, digest and observed capture time. Old accepted originals remain addressable under the retention policy.
- `ingest_requests`: caller-supplied idempotency key, source ID, durable queue/staging/committed/failed/canceled state and retry count. A completed receipt cannot be silently rewritten as a new logical import.

The fixture intentionally omits schema details for auth/identity provider accounts, users, links, extracted text, search indexes, model inference, vector storage and remote sync. Those require separate acceptance in #102/#103/#106/#107/#50. The SQL fixture is a design validation artifact, **not a production migration or an accepted long-lived wire format**.

### Originals and paths

Plan an owner-chosen data root with an **independent** catalog file and folders:

```text
PraxisData/
  catalog.sqlite               # plus SQLite-managed WAL/shm files while active
  objects/aa/bb/<sha256>        # path derived only from a verified digest
  staging/<opaque-id>.part      # unpublished input bytes
  derived/                      # replaceable extraction/search data
```

Do not store original media inside the catalog and do not treat the watched input folder as the database. Process sources through a bounded stream (64 KiB chunks in the research probe), with caller-configurable total capacity and per-item limits. A source path must be selected and validated, with explicit behavior for symlinks, permission loss, changes mid-read, unplugged volumes, sparse files, hard links and import races. Never execute imported code or process an archive with unbounded recursion.

## Publication, recovery and deletion contract for #100

1. **Observe source and request** using a unique source identity and idempotency key. Check configured scope and quota before reading; never move/delete the owner input.
2. **Stage original** into an owner-private temporary file on the same filesystem as the target object; hash incrementally, bound bytes/time/cancellation, then flush and verify the content and digest. Unexpected file changes or exhaustion fail with visible status, retaining neither a falsely accepted object nor an incorrect receipt.
3. **Publish verified object first** with a non-overwriting same-filesystem operation, independently verifying an existing digest path if deduplicated. Staging leftovers or an orphan published object after a crash are recoverable housekeeping, **not** evidence that an import committed.
4. **Commit catalog transaction second**: establish/locate the source and item, append a version, record the object reference and finalize the request receipt atomically. A failed SQL transaction cannot return a successful import. Catalog rows must never claim a missing/incorrect object exists; verify on read and during recovery, and refuse corrupt references.
5. **Restart recovery** validates schema version, retained idempotency receipts and pending/staging jobs. Resume or retry only with revalidated data; sweep unreferenced staged and published files only after a tested grace/retention policy and backup coordination. Never delete an object simply because one source was revoked if another source/version references it.
6. **Revoke or forget source** via a catalog state transition and explicit owner policy. Search/derived/cache visibility is denied immediately for revoked sources, while irreversible deletion of originals is separately controlled by history/retention/backup requirements.
7. **Export and restore** must coordinate the SQLite backup mechanism with a consistent set of referenced object hashes. Copying only `catalog.sqlite` while WAL is active or omitting the object tree is **not** a backup. Keep the existing portable archive specifications readable; a larger-format streaming export requires a separately versioned proposal and byte-level round-trip tests.
8. **Migrate** only through explicit schema versions, migrations and rollback/backup evidence. The fixture's `PRAGMA user_version=1` is a research format marker, not permission to treat it as production schema version 1.

SQLite transactions cannot atomically commit a separate filesystem rename. Publication order above ensures process interruption can leave only unreferenced or incomplete work *if* the filesystem obeys flush/rename ordering. Abrupt power loss and unplugging a USB/Thunderbolt drive require fault injection on the actual target OS/filesystem before stronger guarantees or automatic cleanup. Use bounded diagnostics rather than exposing source text or absolute private paths in logs.

## Owner-local privacy boundary

The catalog, original media and indexes are **plaintext** in this first design. Restrict the data directory to the active OS owner (and prefer OS disk encryption where configured), but acknowledge that a compromised logged-in owner or process with equivalent filesystem access can still read them. Never imply encrypted storage, multi-user isolation, remote authorization or zero-cost/private cloud inference.

Do not start a non-loopback listener or allow browser pages to connect with implicit trust. #107 must separately define caller identity, origin/rebinding protections and a consent/pairing model before an extension or MCP agent receives any content. Local direct ingestion requires no externally reachable service; outward network fetch, speech/LLM providers and sync remain explicitly disabled until their independent authorization decisions (#50/#104/#109/#117).

## Empirical probe and what it does **not** prove

The [probe](../../scripts/research/local_catalog_probe.py) evaluates embedded SQLite against an append-only JSONL event baseline with a repeatable generated corpus, version updates, cold reopen/replay, and per-candidate stored bytes. It streams a **physically written 64 MiB** synthetic original using **64 KiB** buffers and verifies its SHA-256 after reopening; a separate **2 GiB sparse input** is read in the same fixed-size chunks to exercise large-file length handling without consuming 2 GiB of actual disk blocks. The [constraint tests](../../scripts/research/test_local_catalog_probe.py) exercise schema version, multiple sources sharing one digest, uniqueness, failed references, rollback, reopen, metadata backup and corrupt-catalog rejection.

Run from the repository root:

```sh
python3 -m unittest discover -s scripts/research -p 'test_*.py' -v
python3 scripts/research/local_catalog_probe.py
```

### Recorded Linux CI sample

[CI run #120](https://github.com/berniemackie97/Praxis/actions/runs/42513992076) executed the same generated workload on SQLite **3.45.1**: 4,000 source/item entries, 100 updated revisions, a physically written 64 MiB original and 2 GiB of streamed sparse input.

| Observation | SQLite schema fixture | JSONL append baseline |
| --- | ---: | ---: |
| Initial records | 0.0685 s | 0.0168 s |
| 100 individually synced revisions | 0.0426 s | 0.0350 s |
| Reopen/count vs event replay | 0.0007 s | 0.0120 s |
| Metadata bytes on disk | 1,982,464 | 832,300 |

These samples show SQLite **does not outperform the simpler flat log in initial append throughput or file footprint** on this synthetic workload. The selection rests instead on transactional multi-entity consistency, uniqueness/foreign keys, schema migration and indexed queries without building a separate log compactor and referential integrity layer.

Durations from GitHub-hosted runner disks are descriptive, **not** platform performance targets or a comparable .NET provider benchmark: JSONL records are denormalized, unlike the multi-table SQLite schema. They illustrate the operational cost of replay versus transactional indexing. The 64 MiB written fixture and 2 GiB sparse read demonstrate fixed-buffer processing and large-length arithmetic. A sparse file is not a fully written 2 GiB media transfer: neither benchmark proves that a 3 GiB video, unplugged external SSD or large-library restore passes on macOS, Linux and Windows. Full multi-GiB import, busy-writer, cancellation, retention and power-loss tests remain hard acceptance criteria for #100/#40.

## Consequences and next slice

The next bounded task is [#100](https://github.com/berniemackie97/Praxis/issues/100): select/lock a supported .NET SQLite provider, implement a single-owner catalog and verified streamed object publication (not a background watcher), then prove restart/idempotency, same-bytes/different-source, invalid path/digest, backup/restore and out-of-space behavior on real filesystems. **Do not** introduce a generic repository framework, ORM plus parallel SQL implementation, vector store or multi-user auth abstraction just to satisfy the spike.

The existing archive transfer tests remain unchanged. The broader pipeline (#102), safe extractors (#103), search (#106), first-party UI (#108), hosting/security decisions (#50) and operational recovery (#40) remain open.
