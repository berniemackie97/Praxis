# ADR 0004: Portable note identities and revision archives

**Status:** Accepted for local Markdown note exchange; attachment-bearing collection archives are defined separately in [ADR 0005](0005-attachment-collection-archive.md).

## Context

Obsidian filenames can change, files may be edited offline, and future clients must be able to read the same notes and their revision history. A filename cannot serve as a permanent record identity. A database-specific export would make recovery dependent on the server's storage technology.

## Decision

- Each note has a randomly generated UUID that remains unchanged when the note is renamed.
- Every edit or rename creates an immutable, numbered revision. Revision numbering starts at one and is contiguous within a record.
- A note's current path is metadata, not an archive entry path or database identity. Paths use normalized, portable relative Markdown names with forward slashes. Current paths must be distinct case-insensitively within a collection.
- Local export/import uses a ZIP archive of original Markdown text revisions and a versioned JSON manifest. Markdown is serialized as UTF-8; the text, line endings, Unicode, filename history, identity, and revision sequence round-trip.
- Revision entries are addressed by note UUID and revision number, never by untrusted manifest paths. The manifest includes each entry's UTF-8 byte length and SHA-256 digest. The digest detects corruption but does not authenticate the archive: an attacker who can replace the content and manifest can recalculate it.
- Readers reject unknown manifest versions, unrecognized fields, unexpected ZIP entries, invalid paths, duplicate identities/paths, noncontiguous revisions, oversized data, invalid UTF-8 and mismatched hashes. They do not extract ZIP paths to disk.
- Writers and readers leave caller-owned streams open. Callers are responsible for committing a completed archive atomically; an interrupted ZIP write is not a valid backup.

## Format v1

ZIP entries:

- `manifest.json`: UTF-8 JSON object with `formatVersion: 1` and an array of notes.
- `notes/<uuid-as-32-hex>/<revision-as-8-decimal-digits>.md`: UTF-8 Markdown for each historical revision.

Each note manifest entry includes `id` and ordered `revisions`. Each revision contains `number`, `path`, `entry`, `length`, and `sha256` (lowercase hexadecimal digest).

Format v1 supports **Markdown notes only**. Attachment identity, binary byte preservation, deletion/tombstones, encryption, cross-device merge algorithms, persistence, and wire protocol are explicitly out of scope and tracked in #16 and #17. Current notes can be exported and imported, but full-vault disaster recovery remains blocked on attachment and key-recovery milestones.

## Limits

A single archive supports at most 10,000 notes, 50,000 revisions, 8 MiB per note revision, 8 MiB of manifest, 256 MiB of uncompressed Markdown and manifest bytes combined, and 288 MiB of ZIP file input. Import requires a readable, seekable stream; callers receiving a network stream must first spool it with an independent size limit. These are protective bounds for the initial interchange format, not product-wide capacity promises. Large-scale streaming/partitioning needs measured requirements and a separate design.

## Consequences

- Identity and history no longer depend on Obsidian's file layout.
- No persistence engine or network API is chosen by this decision.
- The archive is an interoperable snapshot, not a synchronization transaction log.
- Clients must retain UUID metadata outside filenames; a future editor adapter determines how to map files to records.
- Path validation intentionally favors cross-platform portability over accepting every possible filesystem filename.
- Compatibility tests must preserve archived format v1 as later formats are introduced.
