# ADR 0005: Attachment identity and portable collection archives

**Status:** Accepted for local collection interchange. Deployment, synchronization and device key custody remain open.

## Context

The v1 note archive preserves Markdown revision text and stable note IDs, but it cannot recover binary originals, attachments shared between notes, or a link's identity when an attachment is renamed. A collection export must contain everything it claims to restore, without trusting archive paths or accepting changed binary payloads silently.

## Decision

- Keep the version-one note archive readable and writable through `NoteArchive.Read` and `NoteArchive.Write`. These operations are **notes-only** and never silently discard binary content from a newer archive.
- Add `NoteArchive.WriteCollection` and `NoteArchive.ReadCollection`. The collection reader accepts version-one note archives (with zero attachments) and version-two collection archives.
- An attachment owns an independent UUID, a normalized portable relative path and the original bytes. Renames preserve the UUID and bytes. Byte arrays supplied by callers and returned from `ReadContent` are copied; callers cannot mutate the stored content.
- Attachments may have identical bytes without sharing identity. Their ZIP payload is addressed by the lowercase SHA-256 of the bytes, so identical payloads occupy only one entry within the archive.
- `AttachmentReference` stores a stable (noteId, attachmentId) pair. References may share an attachment between notes, must resolve to included records, and cannot be duplicated. Unreferenced attachments remain valid assets and survive export.
- Current note and attachment paths must not collide case-insensitively. Manifest paths are metadata only; archive entry paths are generated from UUIDs and digests. Readers never extract entries to the filesystem.
- Readers reject unsupported versions, unknown fields, malformed or duplicated entries, invalid paths, dangling/duplicated references, missing or altered payloads, inconsistent lengths and archives exceeding resource bounds.
- Manifest SHA-256 checksums detect accidental corruption and casual tampering. They **do not** authenticate the source; attackers who can replace the archive and its manifest can recompute every digest.

## Collection format v2

The ZIP contains:

- `manifest.json` with `formatVersion: 2`, `notes`, `attachments` and `references`.
- `notes/<uuid>/<revision>.md`: the existing original Markdown revision entries.
- `attachments/<lowercase-sha256>`: one binary payload for each distinct digest.

Each attachment manifest entry includes `id`, `path`, `entry`, `length` and `sha256`. Each reference includes `noteId` and `attachmentId`. Version-one archives retain their original two fields (`formatVersion` and `notes`) and are never rewritten in place.

## Capacity and lifecycle

A complete archive is limited to 10,000 notes, 50,000 note revisions, 10,000 attachments, 50,000 note/attachment references, 8 MiB per note revision, 32 MiB per attachment, 8 MiB of manifest, and 256 MiB of total uncompressed unique payload bytes plus manifest. ZIP input is limited to 288 MiB and must be a readable, seekable stream.

This local format is a **bounded snapshot**, not a filesystem backup engine. Callers must write it to a temporary destination and publish it atomically after a successful close. Collections and attachment payloads currently reside in process memory; larger asset workloads require a separately designed streaming/durable persistence contract.

`ReadCollection` restores note histories, attachment original bytes and explicit identity links. It does not reconstruct an Obsidian vault or rewrite Markdown links. Those adapter details belong to #22 and #25. The bytes have no independently verified backup copy until a caller persists a completed archive.

## Excluded

No deletion/tombstones, cross-device conflict resolution, access control, key management, encrypted archives, hosting, or remote synchronization is introduced here. They remain gated by #13, #15, #17, #40 and #50. No unreviewed server endpoint accepts private data.

See [ADR 0004](0004-portable-note-archive.md) for the original Markdown revision format.
