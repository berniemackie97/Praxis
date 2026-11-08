# ADR 0011: Read-only attachment embed audit

**Status:** Proposed for #25 / #57.

## Decision

The version-two collection archive stores immutable note histories, original attachment bytes and explicit note-to-attachment ID pairs. The existing folder import preserves Markdown text verbatim but cannot assert that embedded attachment paths refer unambiguously to the archived file identities.

`LocalVaultAttachmentLinks.Inspect` is a read-only, deterministic audit over one validated `KnowledgeCollection`. It extracts simple local `![[file.ext]]` / `![[path/file.ext|display]]` embeds and inline Markdown image destinations `![label](path/file.ext)`; it does not mutate the collection, original notes or stored references. `vault links <archive.zip>` reads a validated archive, reports the distinct resolved ID pairs, and exits nonzero with source-path diagnostics when a target cannot be resolved uniquely.

Wiki paths can be vault-root paths, explicit relative paths or unqualified unique attachment basenames. Markdown paths accept vault-root and note-relative candidates and must resolve to exactly one attachment. URL-encoded Markdown filenames are decoded before lookup. The resolver treats attachment paths case-insensitively under the same portable archive rules. Relative paths that escape the vault, missing targets and ambiguous bare filenames are reported rather than guessed. External URL embeds and ordinary non-embedded links are not attachment references. Code fences and matched inline backtick code spans are ignored.

## Boundaries

This is **not** a complete CommonMark or Obsidian parser. It does not handle reference-definition images, nested destination syntax, image size/link metadata beyond the simple patterns above, Obsidian aliases/frontmatter, arbitrary escaped markup, HTML embeds or semantic note-to-note links. Results must not be used as proof that all links in a vault have been inspected. It deliberately does not insert discovered references into the archive: doing so without provenance and a removal/update policy could leave stale references when an embed is edited or removed. Previously authored explicit references remain unchanged.

A subsequent slice must define a source-aware stored reference model and validate it against real Obsidian vault behavior, including stale links after rename, repeated import, deletion/tombstones and editor-specific syntax. None of these operations introduce a remote endpoint or alter plaintext-at-rest limitations.

## Verification

Unit tests cover relative and root paths, unique basenames, URL-encoded image paths, ambiguous and missing targets, vault escapes, code regions, external links and read-only behavior. The separate-process CLI smoke audits both the baseline and updated archive and asserts that unresolved links fail with diagnostics. Issues #22, #25 and #57 remain open for complete integration and device verification.
