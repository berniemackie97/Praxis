# ADR 0026: Verified technical text ingestion before structural interpretation

**Status:** Proposed for #149/#153. Runtime changes require owner verification.

## Decision

Treat a bounded selection of source and textual assembly/disassembly extensions as **text inputs**, not as automatically understood programs. The existing `source-text-v1` processor verifies immutable original bytes and SHA-256, limits input to 1 MiB, strictly decodes UTF-8 (optional BOM), rejects control characters and known non-text signatures, and publishes a revision/digest-bound derivative to the existing rebuildable FTS5 search. No imported content is executed, compiled, or treated as an instruction.

Initial explicit extensions: `.cs`, `.csx`, `.fs`, `.fsx`, `.c`, `.h`, `.cc`, `.cpp`, `.cxx`, `.hh`, `.hpp`, `.hxx`, `.asm`, `.s`, and `.lst`. Existing `.txt`, `.md`, `.markdown` remain supported. A file's extension is an eligibility hint only; unknown formats, binary originals, invalid UTF-8, oversized files and malformed inputs retain visible unsupported/failed states. The produced text is source evidence, **not** a verified symbol graph or a claim about machine behavior.

For pre-existing catalog entries whose *current* source revision was previously marked `unsupported_format`, the processor can transactionally replace only that unsupported derivative and requeue the same item/revision. It excludes failed, canceled, revoked, superseded, genuinely unsupported-by-policy and already completed work. The original remains unchanged; reprocessing commits a new text derivative and FTS entry under the existing revision. This is an explicit compatibility repair for earlier imports, not a generic permission to retry or mutate historical results.

## Further work and research gate

Issue #152 must evaluate concrete AST/export contracts and an authorized evaluation corpus before #153 adds structured symbols/call edges. A verified text match is not a call edge, symbol identity, declaration, version equivalence or citation to an exact source span. Symbol extraction must record explicit artifact-relative spans, parser version and ambiguity, and cannot merge code/disassembly by name alone. Avoid cloud fallback and additional parser dependencies until measured.

## Verification

The new test cases cover C#/C++/ASM/listing search, source digest/original identity, recovery from prior unsupported states, idempotent repeat/restart, revocation/supersession fences and binary-signature/malformed-UTF8 rejection. CI and physical macOS acceptance remain outstanding; do not close #149/#153 or the intelligent MVP gate #170 based on this text-only checkpoint.
