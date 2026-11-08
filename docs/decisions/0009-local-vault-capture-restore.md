# ADR 0009: Local vault capture and no-overwrite restore

**Status:** Proposed for issue #25. This defines local plaintext folder/archive interchange, not a live Obsidian adapter, synchronization engine, or encrypted backup service.

## Decision

`LocalVaultExchange.Capture(vaultDirectory)` recursively reads regular files, including hidden configuration, and constructs an existing `KnowledgeCollection`. Files ending in `.md` become one-revision `NoteRecord`s; other files become `AttachmentRecord`s containing original bytes. Each receives a UUID within that collection. Independently rescanning a folder creates new identities, so this is not an incremental identity-preserving importer.

`CaptureArchive` writes a version-two collection ZIP outside the vault. The destination's parent must exist and the output must not exist. It stages a uniquely named file alongside the destination, flushes it, and publishes through a non-overwriting move.

`RestoreArchive` reads and validates the full archive before creating output. `RestoreCollection` preflights all file paths and sizes, writes the current note revisions and original binaries into a sibling temporary directory, and publishes only by moving the completed directory into a previously nonexistent target. It never replaces an existing vault or any of its files.

Markdown content, frontmatter, wikilinks, relative Markdown links and original line endings are preserved **verbatim**. Links are not parsed, resolved or rewritten. `AttachmentReference` records are not inferred by scanning Markdown; they remain empty on a fresh capture. Existing explicit references and old note revisions stay in collection archives, while a restored folder contains only current files.

## Validation and capacity

- Normalize source-relative paths to portable NFC `/`-separated paths; reject traversal, reserved names and unportable filename characters.
- Reject case-insensitive path or directory collisions, file/directory prefix conflicts, symlinks, junctions, reparse points and nested empty directories rather than silently omitting them. An entirely empty vault is valid.
- Markdown must be valid UTF-8. A valid BOM and line endings are preserved byte for byte.
- Enforce 10,000 notes, 10,000 attachments, 20,000 directories, depth 64, 8 MiB per note, 32 MiB per attachment and 256 MiB total source payload before export or restore. The ZIP format has its own manifest and archive-size bounds under ADR 0005.
- Test representative nested vaults, both link styles, hidden configuration, binary corruption, no-overwrite destinations, invalid UTF-8, size bounds, empty directories, symlinks and cross-platform naming conflicts.

## Privacy and durability limits

These methods write **plaintext**. The caller must protect source, archive and destination folders. No remote API, network upload, identity provider, encryption, sharing, secure deletion or automatic restore into a live workspace is included.

Filesystem inspection is not secure against a malicious local actor replacing paths after validation; source and destination parents must be trusted. Flush and same-filesystem publication protect against ordinary interrupted processes but have not been validated against abrupt power loss, all filesystems or storage-provider directory metadata semantics. Concurrent source edits are not snapshotted atomically. File permissions, timestamps, xattrs, symbolic links and empty subdirectories are not retained. Replacing an existing vault requires a separately reviewed workflow.

Issue #25 remains open for real Obsidian adapter integration, stable identity across imports, semantic links/metadata reconciliation and device-level backup/restore verification. See [ADR 0005](0005-attachment-collection-archive.md) for the ZIP format and content integrity rules.
