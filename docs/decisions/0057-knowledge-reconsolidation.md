# ADR 0057: Knowledge reconsolidation

**Status:** Proposed — [#463](https://github.com/berniemackie97/Praxis/issues/463), first slice. Related: #445, #455, #456.

## Context

Entity types were decided once, when a proposal was linked, and then only changed if new mentions of the same entity arrived. Three things made earlier judgments wrong without anything revisiting them:

- **Rules change.** The work-item correction (PR #464) and the intake noise gate (#462) both change what is true about data already in the catalog.
- **Evidence disappears.** Revoking a source removed its mentions from every view, but the stored inferred type kept reflecting the revoked evidence. Its queued processing jobs also stayed counted as waiting although no worker could claim them.
- **The owner teaches.** The owner corrected the same kind of mistake repeatedly (four C++ classes retyped by hand on 2026-11-14), and nothing generalized the lesson.

## Decision

Add a **reconsolidation queue** and a **revision log** (catalog v32) and a background `ReconsolidationRunner` in the worker. No model calls; every check is deterministic SQL over authorized evidence (eligible proposals of active sources).

**Triggers** put an entity in `reconsolidation_queue` (one row per entity):

| Trigger | Source |
|---|---|
| `rules` | `EntityTypeSanity.Version` differs from `runtime_settings.knowledge_rules_version` at worker start; every inferred entity is queued once. |
| `revocation` | SQL trigger on `sources.status`; also cancels the source's queued jobs. |
| `mentions` | Linking new entity mentions, and an SQL trigger on every new relation (model or owner). |
| `owner_lesson` | SQL trigger when the owner sets a type, recording the type Praxis held before. |

**Checks** in each bounded pass (200 entities):

1. **Refresh inferred type** — the most common type among remaining visible mentions, with the work-item correction. A change is *applied* (`type_origin` stays `inferred`) and logged with before, after, trigger and basis.
2. **Relation signature** — an entity without deterministic code evidence whose confirmed relations only code can have (`inherits_from` → class; two or more `calls` → function) gets a *suggested* revision.
3. **Owner lesson** — after the owner retypes E from X to T, inferred entities still typed X that relate to a shared neighbor in the same role (same predicate and direction) as E get a *suggested* revision to T, with the shared role in the basis. At most 25 per lesson.

Owner and verified types are never revised. Accepting a suggestion applies it as an owner `set_type` decision (and therefore becomes a lesson itself); dismissing it prevents the same proposal from being raised again. The desktop protocol exposes `knowledgeRevisions` and `knowledgeRevisionDecide`; the macOS Knowledge workspace has a **Revisions** tab.

## Boundaries

- Read-time corrections in the graph remain the source of truth for display; reconsolidation keeps stored values and the review queue consistent with them.
- No relation, merge or region revisions yet; the log's `field` is limited to `entity_type` and can widen with #455 region revisions.
- No embeddings or model passes. A later slice can add a model "second look" for entities with conflicting evidence, under the existing disclosure policy and owner-set limits.
- Region and graph renderers do not yet animate revisions; #455's revision stream will carry them.
