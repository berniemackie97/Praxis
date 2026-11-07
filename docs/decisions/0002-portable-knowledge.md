# ADR 0002: Portable canonical knowledge

**Status:** Accepted in principle; record schema pending

## Context

Knowledge must outlive any editor, search provider, database, inference model, or deployment.

## Decision

Human-authored notes must be exportable as Markdown, preserving metadata and attachments. Original imported files remain recoverable. Embeddings, extracted text, search indexes and generated summaries are derived data, never the sole surviving content.

## Consequences

- Indexes are rebuildable from canonical records.
- Stable identity, revisions, rename/delete semantics and attachment references need a specification before synchronization.
- Full export/import and restore are acceptance tests.
