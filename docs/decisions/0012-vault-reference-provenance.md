# ADR 0012: Provenance-separated vault attachment references

**Status:** Proposed under #25 and #57.

## Scope

An archive may contain two distinct kinds of attachment reference:

- **Explicit references:** asserted by a caller and retained across local vault rescans, even if there is no corresponding textual embed.
- **Vault-derived references:** inferred from supported Markdown embeds in a local vault and re-evaluated against the current note and attachment identities on each incremental scan.

The two lists are independently validated and kept separate. A pair may appear in both lists, deliberately. Removing a textual embed must never remove a caller's explicit reference.

## Archive format

The version-two collection manifest remains compatible and continues representing only explicit `references`. A collection with active vault-link tracking writes format version **3** and includes a separate `vaultReferences` array, even if empty. Version-three archives retain the same note history, original attachment payload, SHA-256 and explicit-reference structures as version two. Reads reject malformed or dangling references and combined counts exceeding the established reference limit. Version-one note archives and version-two collections continue to read as before. The normal `WriteCollection` path writes version two for untracked collections and version three for tracked collections; no new database or sidecar is introduced.

`KnowledgeCollection.VaultReferences == null` means tracking has never been enabled for this snapshot; an empty non-null list means tracking is enabled but there are no currently resolved embeds. Archive reads and writes preserve this distinction.

## Capture and rescan

Tracking is **opt-in** for a first capture or for upgrading a version-two baseline:

```sh
dotnet run --project tools/Praxis.VaultCli -- capture /vault /backups/first.zip --track-links
dotnet run --project tools/Praxis.VaultCli -- plan /vault /backups/first.zip
dotnet run --project tools/Praxis.VaultCli -- update /vault /backups/first.zip /backups/next.zip
```

Once enabled in the archive, tracking continues on subsequent updates without repeating the flag. The update reconciles paths and stable identities first, then recomputes tracked pairs from the new note text and current attachment paths. An unchanged rescan preserves pairs and histories, edits add pairs to the tracked list and attachment renames keep stable IDs when mapped explicitly.

An unresolved or ambiguous supported embed stops tracked capture, planning and update **before publication**. A tracked pair that disappears is returned by the non-writing `plan` operation with its exact note/attachment UUIDs, current paths and removal count. `update` always refuses unapproved removals, including incomplete, duplicate or supersets of approval pairs.

The operator reviews the reported identities and writes an explicit JSON approval file **outside the vault**:

```json
[
  {
    "noteId": "11111111-1111-1111-1111-111111111111",
    "attachmentId": "22222222-2222-2222-2222-222222222222"
  }
]
```

The displayed IDs above are illustrative; use the actual UUID pairs from that `plan` result. Pass the file when publishing:

```sh
dotnet run --project tools/Praxis.VaultCli -- plan /vault /backups/first.zip
dotnet run --project tools/Praxis.VaultCli -- update /vault /backups/first.zip /backups/next.zip --approve-unlinks /backups/reviewed-unlinks.json
```

The approval must exactly match **every** disappearing pair, with no extras or duplicates. A source edit between preview and update that removes an additional pair invalidates the approval. The approved list is bounded to 4 MiB / 50,000 pairs. The previous archive remains unmodified and new archives use staged, non-overwriting publication. A mapped rename does not rewrite Markdown: if the source still references the obsolete path, tracking refuses the update until the user fixes or intentionally changes it.

## Limits and release gates

The scanner is deliberately the bounded, partial syntax implementation in ADR 0011. It does not prove complete Obsidian/CommonMark compatibility or recognize every embed form. The reference lists are not security permissions or authorization grants. Source content and archives remain plaintext; hidden vault settings and private data must be handled privately.

The format does not represent file deletion/tombstones, preserve arbitrary filesystem metadata or synchronize devices. It neither changes live Obsidian files nor introduces a watcher, remote endpoint, external package, account, or background service. The real editor adapter, semantics beyond supported embeds, valid detach/restore behavior and device-level acceptance remain outstanding in #22, #25 and #57. The hosting and privacy approvals in #50 remain open and are **not** satisfied by local-link evidence.

## Verification

Tests cover v1/v2 compatibility, version-three round-trips, stable identities and histories, initial tracking, automatic tracking continuity, added and removed embeds, explicit reference retention, mapped attachment renames, stale-link refusal, malformed/ambiguous links, no-output failure paths, and no vault side effects. The separate-process CLI smoke verifies the archive's tracked-reference count, ref removal approval, incremental update and restore.
