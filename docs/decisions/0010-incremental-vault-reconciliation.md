# ADR 0010: Explicit incremental vault reconciliation

**Status:** Proposed for issue #25. This decision extends local folder/archive interchange without introducing a vault watcher, a database, or remotely accessible synchronization.

## Objective and persistence contract

An initial `LocalVaultExchange.CaptureArchive` creates a portable collection snapshot with stable note and attachment IDs, note revision histories, and explicit references. A subsequent `PlanUpdate` compares the current regular files in the original vault against that previous archive, and `UpdateArchive` publishes a **new** collection archive with the reconciled state. The previous archive is never mutated. Both the input archive and the output archive must be outside the source vault.

The reconciliation algorithm matches files by their portable, case-insensitive paths unless a caller supplies an explicit rename mapping. An unchanged file retains its prior record and revision history; a changed note appends exactly one immutable revision, and a changed attachment keeps its ID while replacing its archived original bytes. A path change updates the record's path and appends a note revision even if text is unchanged. Explicit `AttachmentReference` pairs from the previous archive retain their IDs across all these operations. New files get new IDs. Fresh capture archives and previously generated version-two archives remain readable.

**No automatic rename inference is permitted.** Matching byte hashes cannot reliably distinguish a rename from a delete followed by an unrelated add, especially for duplicate file contents. A caller may provide one-to-one mappings of source to destination paths. The source must exist in the old archive; the destination must exist in the captured folder. Redundant, duplicate, ambiguous, cross-file-type, or invalid path mappings are rejected. Explicit mappings support name swaps and case-only renames. A rule with `isFolder: true` expands a captured folder move into file mappings derived only from **known prior archive paths**. When folder rules are present, all move events are evaluated in order, allowing folder chains and subsequent file moves; missing, duplicate, overlapping, invalid or unresolved destinations refuse publication. Pure file-only mappings retain their prior direct source-to-destination semantics.

A prior file that is no longer matched makes the update fail **without publishing a new archive**. The user must restore it or explicitly map it to a replacement location. The current archive schema does not express deleted/tombstoned notes or attachment retention, so treating absence as a committed deletion would lose history and references. Deletion approval and tombstone retention require a future versioned change contract.

## User workflow

The local CLI adds `plan` (read-only preview) and `update` (write to a new archive):

```sh
dotnet run --project tools/Praxis.VaultCli -- plan /path/to/vault /backups/previous.zip
dotnet run --project tools/Praxis.VaultCli -- update /path/to/vault /backups/previous.zip /backups/next.zip
```

To tell the importer about a moved file, prepare a UTF-8 JSON array **outside the vault**:

```json
[
  { "fromPath": "Notes/old.md", "toPath": "Notes/new.md" },
  { "fromPath": "images/old.png", "toPath": "images/new.png" }
]
```

A folder move is explicitly marked in the same JSON file:

```json
[
  { "fromPath": "Notes", "toPath": "Archive/Notes", "isFolder": true }
]
```

This preserves the old note and attachment IDs for all previously archived files under `Notes/` if they now exist under `Archive/Notes/`. It does not infer a move from matching hashes or auto-accept disappearing files. An empty folder has no archived file identity to map and does not qualify.

Pass `--renames /backups/renames.json` to either command. The mapping is limited to 256 KiB and 20,000 entries; its paths are validated using the same portable path rules as the archive. `plan` reports added, content-updated and renamed item counts without writing any archive; it does **not** reserve identities for newly discovered files. `update` reads and validates the entire baseline, captures the current folder, reconciles identities and only then writes its new archive using the established temporary-file and non-overwrite publication.

The caller should review the plan and any missing/ambiguous paths, preserve the previous archive as a rollback point, then verify and independently restore the new archive before replacing any working vault. Never replace an existing snapshot in place to advance the baseline.

## Security, recovery and compatibility limits

This contract assumes a trusted, caller-owned local filesystem and produces unencrypted archive files. It does not defend against hostile source edits between enumeration and reading, concurrent editor writes, malicious symlink swaps after validation, disk-full/power-loss durability on all filesystems, or an attacker replacing both a manifest and its checksums.

A note's Markdown is preserved unchanged; user-authored wikilinks, Markdown links and embedded asset paths are **not rewritten or semantically validated** when a file is explicitly renamed. Old explicit reference pairs retain IDs, but newly discovered Markdown link references are not inferred. Folder permissions, timestamps, extended attributes, symbolic links and nested empty directories remain unsupported under ADR 0009.

This is an **incremental local snapshot workflow**, not a live Obsidian plugin, a canonical multi-device journal, or a server-side API. There is no checkpointed watcher, automatic file deletion, merge of concurrent archive branches, source-level ACL, trusted host policy or real-device recovery proof. A stale previous archive cannot be detected without a separate authoritative baseline record.

## Evidence

Tests exercise unchanged rescan idempotence, historical revisions, binary edits with preserved references, explicit renames and name swaps, case-only moves, new files, changed file types, duplicate/missing mappings, implicit rename refusal, tampered archive, no-overwrite publication and exact filesystem restoration. The executable smoke test runs capture → plan → update → verify → restore with an explicit binary rename, and checks that a missing prior file cannot silently disappear.

Issue #25 and its parent #57 remain open for live Obsidian adapter behavior, complete semantic link mapping, safe versioned tombstones, supported-device permission and recovery verification, and the remote privacy decision #50. ADRs 0005 and 0009 remain the archive and one-shot capture contracts.
