# ADR 0035: Canonical file locators

**Status:** Accepted. Fixes #211. No schema change.

## Context

A file source's identity was its locator, `Path.GetFullPath` of whatever spelling a caller passed. The macOS app passed the inbox through `URL.resolvingSymlinksInPath`, which strips a leading `/private`; the CLI passed `/private/tmp/...`. The same file therefore became two sources, each with its own revisions, proposals and entity mentions, so mentions were counted twice.

## Decision

- **One spelling per location.** `CatalogPaths.Canonicalize` resolves symbolic links in a path's existing ancestors (realpath semantics: `/tmp` becomes `/private/tmp`, `/var` becomes `/private/var`) and appends any missing remainder unchanged, so an offline drive or a file that no longer exists still has a stable form. Selected leaves are still never links (ADR 0019); only ancestors are normalized.
- **Applied at the intake boundary.** `LocalSourceIntake.ValidateSourcePath` returns the canonical form, which covers explicit file and folder imports, folder scans and quick-note capture. `LocalWatchedIntake` canonicalizes its root, so watcher events, scans and stored locators agree. The macOS launcher uses `realpath(3)` for the same reason.
- **Existing catalogs are reconciled once per worker start, before the first scan**:
  - A source stored under a non-canonical spelling is relocated: identity, history and collections are kept and the move is recorded.
  - If the canonical spelling already belongs to another active source, the alias is folded into it. Collection memberships are copied, owner proposal reviews are copied to matching pending proposals (same producer, kind, value, relation ends and evidence text) with review history, and then the alias is revoked.
  - If the canonical source was revoked by the owner, the alias is revoked too.
  - Paths that cannot be inspected are left for a later start.

## Consequences

Entity-level decisions were never affected, because entities are independent of sources. Revoking the folded alias hides its duplicate derived data through the existing source-status filters; its originals and history remain, as with any revocation. Tests now build temporary paths in canonical form (`TestPaths.Temp`), since macOS temp folders live behind `/var`.
