# Native Knowledge Graph — #386

## Product boundary

The Knowledge workspace offers **Browse / Connections** views of the **same** catalog. The existing list/detail and verification views stay in place. The Graph view embeds a first-party SpriteKit scene inside SwiftUI; SwiftUI provides the seed search, collection/source/type/relation/evidence filters, bounded expansion controls and keyboard/VoiceOver entity/relationship list.

The graph reads the private desktop worker's `graphSeeds`, `graphNeighborhood` and scoped `graphRelation` responses. The .NET catalog remains the authority for entity identity, source visibility, revisions, review state, relation predicates and direction. The scene does not write the catalog, infer relationships from proximity, contact a model, or read user files directly. #387 adds current relationship reinspection, guarded owner review and supported source-text navigation. Unsupported media or PDF anchors remain explicitly unavailable.

## Interaction

1. Open **Knowledge → Connections**. A single **Your knowledge** overview counts all currently eligible scoped entities by effective type. This is not a layout of the full graph. Choose a type to narrow discovery, search, page through bounded results, or select a result to intentionally enter its neighborhood. An arbitrary ID-sorted first result must never take focus without owner action.
2. Only the current **one-hop** neighborhood loads. Click a node or select it from the accessible list. **Focus** replaces the current neighborhood with that node's own; **Expand neighbors** adds direct edges while respecting the visible budget.
3. Pan by dragging empty space, drag a node to rearrange its view position, and zoom using the scroll wheel or the toolbar controls. Positions are never persisted as facts.
4. Click an edge or select it from the SwiftUI list to inspect predicate, direction, trust status and exact available source excerpt. An owner assertion explicitly has **no invented citation**. Inactive or denied selections are removed when the projection is refreshed.
5. Filter by active collection, active source, entity type, predicate and evidence status. **Suggestions** is an explicit opt-in; a suggestion is never labeled confirmed. A filter change invalidates outstanding replies and clears the old projection before requerying.
6. Use **Refresh** after intake, approval or revocation changes. Leaving the graph clears the visible projection and detaches/pauses SpriteKit. No graph worker requests continue while that surface is inactive.

## Presentation constraints

- The scene accepts at most **120 nodes** and **120 edges**. Seed pages load at most 32 entities, neighbor pages at most 60 edges. These are conservative *initial UI safety limits*, not measured hardware maxima. The authoritative worker additionally limits each request to 100.
- Nodes are colored by type, and proposed names are muted. Selection and evidence statuses are also expressed in text. Labels are shown for small graphs and focused/selected nodes, and expanded at closer zoom; dense neighborhoods do not label everything at once.
- Layout starts from deterministic stable IDs, applies bounded repulsion, typed grouping and attraction only along **actual returned edges**, and stops after at most 180 simulation steps. Dragging only changes a local view position.
- The scene is a visual surface, hidden from VoiceOver. The SwiftUI list and evidence panel expose the same currently authorized entity and relation identifiers via ordinary buttons, without requiring pointer input.
- macOS Reduce Motion disables the simulation; increased contrast changes line and outline visibility. The screenshot fixture also exercises light/dark and compact window sizes.
- The renderer uses bundled Apple frameworks (SpriteKit and SwiftUI), no remote assets or graph engine package. It remains inside the existing `NavigationSplitView`.

## Verification

**Owner-Mac build and tests, required before declaring #386 done:**

```sh
gh pr checkout <PR_NUMBER>
dotnet restore Praxis.slnx --locked-mode
dotnet build Praxis.slnx -c Release --no-restore
dotnet test Praxis.slnx -c Release --no-build --no-restore
dotnet format Praxis.slnx --verify-no-changes --no-restore
bash scripts/macos/build-desktop.sh --archive-existing
bash scripts/macos/test-worker-session.sh
```

The macOS app's Xcode test target contains graph decoder, merge budget, deterministic layout, Reduce Motion and SpriteKit scene setup/resize tests. Run the normal app-hosted tests and use `scripts/macos/capture-screenshots.sh` to capture synthetic graph layouts in light/dark and compact/reduced-motion conditions, not private catalog content.

Manual checks: focused real knowledge, no edges, suggestions off/on, large/high-degree source, narrow window, resize, drag/zoom, selectable edge, keyboard/VoiceOver list, Reduce Motion, high contrast, leaving and returning to the graph, and revoked/revised sources. Use only approved local catalog data. Do not infer CPU/FPS/resource acceptance from the standalone synthetic benchmark. The formal measured, security and accessibility acceptance is #388.

## Relationship review and current source checks — #387

Select an edge using SpriteKit or the keyboard-accessible relation list. Before displaying detailed evidence, `graphRelation` rechecks the relation ID, the selected endpoint and the current graph scope. Origin, predicate direction, evidence status, proposal quote, source/revision, digest, quote offset and verifier verdict are shown without elevating a suggestion into confirmed evidence. Negative verifier verdicts are visible only with Suggestions enabled and explicitly labeled contradicted. Other visible observations between the same entities remain separate, with no automatic contradiction inference.

**Open current source** first revalidates the relation and requests a digest-bound current `preview`; only then can the matching active Library item/revision/digest navigate to Library. Otherwise the graph shows a bounded extracted-text window only when the exact quote occurs at its recorded UTF-16 offset. The source path is not treated as a PDF page, transaction row or timestamp; unsupported or absent exact anchors are reported, not guessed.

For an eligible proposal, **Accept** and **Reject** reuse the existing `topicReview` request and server eligibility check; rejection requires confirmation and reprojects the graph. While an edge is inspected, the existing workbench heartbeat rechecks its current visibility on a bounded cadence and hides revoked or changed evidence; it does not promise instantaneous invalidation. Owner-authored assertions have no source citation or proposal review buttons. The subject and object inspector shortcuts open the canonical Entities workflow for existing name/type/relationship owner decisions; they do not invent an owner-edge deletion action. **Ask Praxis** prepares a current relation-ID-based draft and switches to Chat; the owner must review the text, selected backend and disclosure policy before sending.

**Verification on the owner Mac:** complete locked .NET restore/build/test/format, Release Xcode build, app-hosted Swift tests, screenshots, plus a real graph session checking accepted and suggested relations, explicit unsupported verdict, multiple observations, source revision and revocation during inspection, rejected proposal removal, denied source preview, no available Chat backend, user-chosen Chat send, compact window and VoiceOver. Issue #388 owns quantified device, resource, security and accessibility qualification.

## Known delivery boundary

This is the first **One Brain** navigation slice (#416/#417/#418), not completion of global connected topology. The overview counts authorized entities; it does not infer project membership, fabricate edges, hydrate the full catalog or claim queued items have finished processing. Browse displays at most 200 most-mentioned entities, grouped by type with the limit stated. Entity details and the graph sidebar group equivalent-looking statements into expandable observations while keeping every underlying relation ID and review action. Owner source review is still enforced by #387's live worker actions. Cross-format PDF pages, media timecodes and financial record anchors are not silently guessed.

A later #416 slice must address a readable, bounded inter-context topology, configurable lenses, navigation history and measured evidence quality. #315 owns intake accuracy and redundancy metrics; #388 owns device accessibility/resource/security qualification.
