# ADR 0058: Folder-derived knowledge regions

**Status:** Proposed — [#455](https://github.com/berniemackie97/Praxis/issues/455) region projection, consumed by [#456](https://github.com/berniemackie97/Praxis/issues/456) Living Cortex and the reworked Knowledge Cosmos and Cellular Atlas.

## Context

All brain styles drew `graphConceptRegions`, which only accepts reviewed or verifier-supported containment relations. On the owner catalog no containment relation had been reviewed (all were strong-tier "documented"), so every style showed no regions at all, and the graph looked broken. Meanwhile the strongest grouping evidence Praxis has before review is structure the owner made themselves: the folders and repositories they put under a watched or selected root.

## Decision

`graphKnowledgeRegions` derives the brain's geography from that structure and `graphRegionMembers` pages a region's entities.

- **Placement:** each authorized file source sits under its deepest watched or selected root (from `capture_request_context`). The top folder is a region, its subfolder a child region; generic code containers (`src`, `apps`, `packages`, …) are looked through to their projects; dot-folders stay configuration; a root's loose files form one region. Repository maps (`intake_projects`) mark project regions and supply their purpose. Region ids hash root-relative paths, so they are stable.
- **Evidence binding:** top regions that share at least three confirmed entities and a tenth of the smaller one join a group region, named after the shared project entity their names contain. On the owner catalog this is one OpenConquer group of notes, client and server.
- **Counts and state:** entities are counted from authorized, scoped mentions; sources whose understanding is still queued are reported as pending, so a region can read as forming instead of empty. Hubs are a region's best-connected own entities, never runtime glue.
- **Links:** confirmed relations between entities of non-nested regions, plus shared entities between top regions. Never proximity or similarity. Capped at the strongest 400.
- **Views:** a dominant region is opened into its children until about a dozen territories show. All three styles draw the same territories, counts, links and colours.

The map is computed in one pass over sources, mentions and confirmed relations and cached per knowledge revision and scope. It takes about 0.5 s on the owner catalog and is free while nothing changes.

## Boundaries

- Folder structure is a starting geography, not the last word: reviewed containment, owner corrections and model-suggested themes should refine and rename regions, and reconsolidation (ADR 0057) should revise them as evidence changes.
- Sources captured outside any watched or selected folder are counted as unplaced, not guessed into a region.
