# ADR 0032: Local model semantic extraction with verified evidence

**Status:** Accepted. Schema v12. Advances #115; model routing remains #31/#172.

## Context

The owner wants every captured source that yields text (notes, Markdown, code, disassembly, captions, later transcripts and web pages) enriched automatically by the local model, with later escalation to stronger models only where a local result is uncertain. Model output must never become fact without review, a missing or busy model must not lose work, and nothing may leave the machine.

## Decision

- **Stage `semantic-extract-v1`** depends on `text-segments-v1`. For each segment (up to 64 per revision) the local model receives versioned instructions, the source name, the section path and the segment wrapped in `<excerpt>` tags marked as untrusted data, and must answer with a JSON-schema-constrained list of up to 5 topics and 12 typed entities (person, organization, project, technology, concept, place, document, event, other), each with a verbatim quote. Context per request is the lower of the profile's limit and 4,096 tokens; output is capped at 768 tokens.
- **Verification before storage.** A quote must be found in its segment, exactly or with whitespace runs treated as equal; an entity's name must appear in the segment; types must be in the allowed set; names are normalized and deduplicated per revision; at most 64 proposals per run. Everything else is counted in `rejected_count`. Stored evidence is the original text span, not the model's quote.
- **Proposals generalize** to `topic` and `entity` kinds with `entity_type` and `evidence_length`, values up to 120 and evidence up to 240 characters; all start `pending`. The heading producer uses the same commit path.
- **Provenance and usage.** Each run records model provider, name and digest (resolved per job, so a model swap applies without restart), input and output tokens, model time, segments examined and total, and rejected count.
- **Runtime availability is not failure.** A disabled profile or an unreachable, missing or memory-starved model raises `ProcessorUnavailableException`; the pipeline returns the job to the queue, refunds the attempt and backs off that processor (30 s doubling to 10 minutes) until the next wake (saving the profile wakes it). Timed-out, truncated or malformed answers lose only their segment; if every segment fails, the job fails with that code.
- **Long runs** renew their lease after every segment and stop without publishing when the source is revoked or the revision superseded.
- **Migrations** run with foreign keys disabled for the batch (SQLite's table-rebuild procedure) and must pass `foreign_key_check` before commit; the proposals table is rebuilt this way.

## Measured on the owner's Mac (M4, 16 GB, Ollama, `huihui-qwen3.5:9b-q4_K_M`)

Three single-segment notes (technical, budget, meeting): 14–36 s per segment, 360–400 input and 200–470 output tokens, all three runs `generated`, every stored proposal located verbatim. Tightening the instructions removed amounts, dates and file extensions that the first version proposed as entities, and reduced time by 15–40%.

## Known limitations

- Type accuracy is limited at 9B: products and codebases are sometimes typed as organizations.
- Quote grounding proves a name is *in* the text, not that the text is truthful. Text that tells the model to report a name it contains (an injection attempt) can still produce a grounded, pending proposal. Proposals carry no authority and require owner review; a stronger-model verification pass (#31/#172) is the planned mitigation.
- Large documents are slow (tens of minutes for 64 segments). Priority keeps new captures ahead of backfill; skipping or summarizing low-value segments is future work measured against #29/#172.
