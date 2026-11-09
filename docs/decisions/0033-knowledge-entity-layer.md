# ADR 0033: Canonical entities, relations and owner-governed resolution

**Status:** Accepted. Schema v13. Implements the core of #116 on top of #115 proposals.

## Context

Model proposals are per source revision. The owner needs one identity per real thing across all sources (notes, code, captions, budgets), the relations between them, and a way to correct the model once and have it stay corrected. Live runs showed recurring type mistakes at 9B (codebases typed as organizations) and that text can steer extraction toward names it contains.

## Decision

- **Entities are identities, not facts.** `knowledge_entities` (display name, type, `type_origin` inferred / verified / owner, status active / merged / rejected) with `entity_aliases` keyed by a normalized name (Unicode compatibility form, collapsed whitespace, enclosing punctuation, leading "the" and trailing possessive removed, upper-cased). No fuzzy matching: different spellings are merged only by the owner.
- **Links are made where proposals are recorded.** Committing a run links each entity proposal to its entity (`entity_mentions`) and each relation proposal to `knowledge_relations`, in the same transaction. Owner corrections move the mention to the corrected name.
- **Visibility is derived, never copied.** `visible_entity_mentions` is the single definition: the proposal is eligible (current revision, active source, verified text), not rejected, and, when flagged, accepted or corrected. Revocation and supersession therefore drop evidence without touching entities or owner decisions; an entity with no visible evidence and no owner type is not listed.
- **Types.** Owner type beats verified beats inferred; an inferred type is the most frequent visible mention type. `TypeConflict` marks an unresolved entity whose mentions disagree (an input for verification, ADR 0034).
- **Owner decisions** (`entity_decisions`, append-only): rename (adds an owner alias; a name owned by another entity requires merge), set type, merge (aliases, mentions and relations move; relations that would become self-references are dropped; the merged entity remains for audit), reject (the name keeps resolving to the rejected entity, so it stays hidden), restore, and owner relations.
- **Relations** use a closed, directional predicate vocabulary (uses, part_of, contains, depends_on, implements, parses, produces, created_by, owned_by, works_on, member_of, located_in, pays, references, related_to). A model relation is kept only between two entities accepted from the same answer, with a quote containing both names. Co-mentioned entities (same segment) are computed at query time.
- **Feedback to the model.** `semantic-extract-v2` passes owner-confirmed and verified types, and rejected names, for names present in each segment as hints. Prompt v2 also adds relations; v1 results stay visible until v2 has run for that revision.
- **Instruction-like evidence** (sentences that address an AI: "ignore previous instructions", "output an entity named ...") is flagged and withheld from the graph until accepted or verified. Flags never delete or reject.
- **Reuse.** A segment whose text is unchanged since the item's previous result reuses those candidates (new proposals with shifted offsets) instead of calling the model; runs record `segments_reused`.

## Measured live (M4 16 GB, `huihui-qwen3.5:9b-q4_K_M`)

Four notes across code, rendering and a meeting: the injected `Hacker Inc` was flagged and withheld; three correct relations (`C3Reader parses PHY`, `OpenConquerClient uses DmapRenderer`, `DmapRenderer parses DMap`); cross-source linking of `OpenConquerClient` and `Conquer Online`. After the owner set both to project, the model typed them as project in the next note; a rejected name it still proposed stayed hidden. Relations raise cost to 26–69 s per segment.

## Limitations

Type accuracy without owner or verifier input remains weak at 9B. Relations attached to a corrected mention keep their original subject name. Fuzzy duplicate detection and proposal-status carry-over across revisions are not implemented; owner decisions persist at the entity level.
