# ADR 0024: Source-grounded proposals and owner review

**Status:** Proposed under #115; implementation unverified.

## Context

Praxis already preserves source revisions, verified content digests, bounded text derivatives and manual source collections. Automatically inserting guessed topics into those owner-authored collections would conflate source observation, machine interpretation and owner intent. A future local Qwen or external approved model must not become a privileged importer merely because it can generate text.

## Decision

Schema v7 records an immutable source-revision processing result in `enrichment_runs` (producer identity, original digest, generated or abstained), candidate assertions in `enrichment_proposals` (source identity through item, revision, digest, kind, value, exact evidence and offset, status), and append-only owner decisions in `enrichment_reviews`. Each table is STRICT and uses foreign keys; the existing coherent catalog snapshot includes all three. Active retrieval and review require a current version/digest, completed text derivative and active source. Historical reviews remain in the private catalog for accountability but do not grant access to revoked or superseded source content through the current proposal API.

The initial local producer `markdown-heading-v1` proposes up to eight literal first- through third-level Markdown headings from verified supported text. It ignores fenced code regions, records exact UTF-16 source spans and abstains when none match. This is a rule/provenance baseline, **not semantic interpretation or a model confidence claim**. It is incremental per item/revision/producer, runs as a leased `markdown-heading-v1` processing job scheduled when the revision's text extraction completes (ADR 0029), and cannot block preservation or indexing merely because no heading is present.

Owner decisions may be accepted, rejected or corrected, with every state change retained in the review history. Idempotent repeats do not append duplicate history. The app surfaces the evidence and producer label before actions. No decision modifies canonical Markdown, original bytes, manual collection membership or linked sources. Inference candidates have no authority to write to the filesystem, change provider settings or grant any tool scope.

## Future processors and review

A local model processor may propose richer topics/entities/summaries only behind explicit opt-in, fixed input/output budgets and versioned producer metadata. All processors must reuse the current-revision digest and source-status fences, encode their own derivation provenance and record abstention/failure without blocking deterministic ingestion. Cross-source assertions and conflict resolution remain #116/#154. External providers require an approved source scope and destination, with cost consent; absence of a local model never causes automatic remote fallback. Context packs and action tools remain separately scoped #143/#117–#119.

## Acceptance and limitations

Tests cover exact source spans, bounded output, fenced-code suppression, abstention, duplicate prevention, review history, source changes, revocation and coherent recovery. Owner macOS integration and multi-OS CI are outstanding. The baseline does not claim automatic semantic comprehension of free text, PDFs, images, code binaries or AI-agent sessions; it must not be represented as the completed #115 feature.

This additive schema is a one-way catalog migration for older clients. Preserve an independently verified backup before upgrading valuable data, and do not silently recreate a missing private store.
