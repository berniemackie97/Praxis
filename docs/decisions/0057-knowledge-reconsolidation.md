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

## Second slice (catalog v33)

- **Merges.** An entity whose name equals another's apart from spacing, punctuation or case gets a *suggested* merge into the better-known one (more mentions), never across differing confirmed types. An expression index on the compact name keeps the lookup cheap. On the owner catalog this finds 30 real duplicates (`OpenConquer.Server` / `OpenConquer Server`, `game server` / `GameServer`, `CGameSocket__ReceiveMsg` / `CGameSocket_ReceiveMsg`).
- **Relations.** A model relation whose endpoints' confirmed (owner or code-evidence) types rule it out, such as a document that `calls` a function, gets a *suggested* retraction. Accepting rejects its proposal; owner relations are never questioned.
- **Lesson replay.** At a rule change, owner type decisions made before reconsolidation existed are replayed once as lessons, using the type the evidence would have given as what Praxis believed.
- **Model second look.** The verification planner already proposes owner-approved model checks for type conflicts; a completed check now re-opens when newer mentions arrive, instead of never.
- **Knowledge revision.** `graph_revision` moves only when something the graph can show changes (triggers on mentions, relations, entities, proposals, verdicts, source status, scope, eligibility). Graph cursors carry it instead of the connection's `total_changes()`, which moved on every catalog write and invalidated open pages while intake ran. `knowledgeWatermark` returns it with the newest revision id; the macOS graph polls it and offers a refresh, and the redesigned renderers will consume the same stream to animate in place.

## Performance (owner catalog copy, 3,839 entities, 1,171 relations)

| Query | Before | After | Change |
|---|---|---|---|
| Overview | 212 ms | 90 ms | set-based effective types (`visible_kinds` CTE) |
| Zones | 973 ms | 234 ms | same, plus materialized relation set before grouping |
| Atlas, 100 links | 1,180 ms | 164 ms | nodes read in one batch instead of ~6 queries each |
| Region, 100 links | 1,618 ms | 305 ms | batch nodes, set-based types |
| Seeds, 40 | 260 ms | 70 ms | batch nodes |
| Reconsolidation, all 3,891 entities | 18.8 s (worst pass 1.3 s) | 2.3 s (worst pass 156 ms) | per-entity technical type and relation lookups by index |

Graph reads now run on the read-only connection inside one snapshot transaction, so they never wait behind intake writes and a revision always matches the rows it describes. The whole-brain atlas pages runtime glue after every other link with two keyset scans, keeping early termination.

## Boundaries

- Read-time corrections in the graph remain the source of truth for display; reconsolidation keeps stored values and the review queue consistent with them.
- Region geography is not revised here. On the owner catalog no containment relation is reviewed or verified, so concept regions are empty; deriving regions from owner repository maps (`intake_projects`) and folders is the first #455 step of the graph redesign, and region revisions follow from it.
- No embeddings. Model involvement stays in the owner-approved verification ladder.
- Renderers show a refresh prompt on revision change; animated in-place updates belong to the renderer redesign.
