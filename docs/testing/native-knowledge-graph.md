# Native Knowledge Graph — #386

## Product boundary

The Knowledge workspace offers **Browse / Connections** views of the **same** catalog. The existing list/detail and verification views stay in place. The Graph view offers a SpriteKit **Cellular Atlas** and a native MetalKit **Knowledge Cosmos** canvas inside SwiftUI; SwiftUI provides the seed search, collection/source/type/relation/evidence filters, bounded expansion controls and keyboard/VoiceOver entity/relationship list.

The graph reads the private desktop worker's `graphConceptRegions` for confirmed top-level project anchors, `graphZones` for the optional entity-type lens and scoped `graphRegion`, `graphSeeds`, `graphNeighborhood` and `graphRelation` for exact detail/evidence. The .NET catalog remains the authority for entity identity, source visibility, revisions, review state, relation predicates and direction. The scene never writes the catalog, infers relationships from proximity, contacts a model or reads user files directly. #387 provides guarded relationship review and source-text navigation.

## Interaction

1. Open **Knowledge → Brain**. Both styles display the same named regions backed by current, scoped containment evidence. A region is sized from its distinct direct members plus its anchor. A known entity can be unplaced and still available in Browse and search. A scope with knowledge but no eligible structure shows an explanation instead of disconnected entities.
2. Click a region or zoom toward it to open its graph neighborhood. In **Subregions**, follow paged structural groups or go **Back**. Direct members without subregions remain in the neighborhood. Zoom outward or choose **Whole Brain** to reload the roots. **Explore by entity type** remains an optional detail lens.
3. Choose **Cellular Atlas** or **Knowledge Cosmos** from **Graph style**. The selection persists locally. Cellular Atlas uses the existing drag-pan, pointer-zoom and Option-drag-rotate controls. Knowledge Cosmos uses drag-to-orbit, Option/right-drag-to-pan, wheel/pinch to move the perspective camera toward a hovered zone and enter it, and outward scroll to return; click a labeled zone to enter immediately. **Fit** returns to the active level's camera framing. The 3D view remains mounted across zone/detail navigation, so camera movement is interpolated even when the bounded detail fetch finishes later. Camera coordinates are presentation-only.
4. Click an edge or select it from the SwiftUI list to inspect predicate, direction, trust status and exact available source excerpt. An owner assertion explicitly has **no invented citation**. Inactive or denied selections are removed when the projection is refreshed.
5. Filter by active collection, active source, entity type, predicate and evidence status. **Suggestions** is an explicit opt-in; a suggestion is never labeled confirmed. A filter change invalidates outstanding replies and clears the old projection before requerying.
6. Use **Refresh** after intake, approval or revocation changes. Leaving the graph clears the visible projection and detaches/pauses the active graphics view. No graph worker requests continue while that surface is inactive.

## Presentation constraints

- The top-level canvas now uses the bounded, confirmed-only `graphConceptRegions` pages, with stable canonical entity UUIDs and explicit member totals, separate from the catalog's type metadata. The client initially loads up to **32 root regions**, and the sidebar exposes additional cursor pages; the graphic draws **at most 64** roots while the accessible sidebar can page farther. The count label distinguishes rendered roots from the authoritative total. `graphZones` still supplies scoped entity-type lens information and observed type-pair link counts, but those type buckets are no longer drawn as the Whole Brain. The optional scoped `graphRegion` lens remains capped at **600 relation observations / 960 endpoints**; focused neighborhoods retain separate **120-node / 120-edge** budgets. Those are provisional resource budgets, not device maxima.
- Nodes are colored by type, and proposed names are muted. Selection and evidence statuses are also expressed in text. Labels are shown for small graphs and focused/selected nodes, and expanded at closer zoom; dense neighborhoods do not label everything at once.
- Region placement comes from scoped `contains` and `part_of` claims with owner, accepted or verifier-supported evidence; project tags play no role. Region placement and member counts are shared across styles. The root page does not supply links between regions. Decorative density marks are not entities; actual authorized relationships and evidence are available in focused views.
- The scene is a visual surface, hidden from VoiceOver. The SwiftUI list and evidence panel expose the same currently authorized entity and relation identifiers via ordinary buttons, without requiring pointer input.
- macOS Reduce Motion disables the focused simulation and pauses the bounded global hub halo animation; increased contrast changes line and outline visibility. The screenshot fixture also exercises light/dark and compact window sizes.
- Both presentation modes use bundled Apple frameworks (SpriteKit, MetalKit and SwiftUI), without remote assets or a graph engine package, inside the existing `NavigationSplitView`. A single graphics view is active at a time. The Metal vertex and fragment shaders are compiled into the app's default Metal library at build time, not compiled from source when changing modes.

The expanded region buffers its pages and only publishes the bounded snapshot once the current source/revision cursor is valid across the complete read. If ingestion changes the catalog mid-read, the UI fails closed with a visible refresh prompt rather than displaying an arbitrarily partial/possibly stale constellation. Search and recenter operate on the rendered graph without replacing the canvas. The context rail can be hidden to maximize graph space.

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

Manual checks: focused real knowledge, no edges, suggestions off/on, large/high-degree source, narrow window, resize, wheel zoom-to-pointer, pinch zoom, blank-space and selected-node drag pan, Option-drag/two-finger rotation, Fit reset, focused-node drag, toolbar zoom after a wheel gesture, selectable edge at both overview and detail scales, keyboard/VoiceOver list, Reduce Motion, high contrast, leaving and returning to the graph, and revoked/revised sources. Use only approved local catalog data. Do not infer CPU/FPS/resource acceptance from the standalone synthetic benchmark. Profile with Instruments on the owner Mac against the same synthetic bounded atlas and a permitted real technical corpus, capturing before/after frame-time distribution, main-thread work, allocations and resident memory while idle, hovering, dragging, scrolling and loading. The deterministic drawable-count test proves an object-budget property, **not** a frame-rate improvement or user acceptance. Formal measured, security and accessibility acceptance remains #388 and responsiveness regression #428 remains open until the real interface passes.

## Relationship review and current source checks — #387

Select an edge using SpriteKit or the keyboard-accessible relation list. Before displaying detailed evidence, `graphRelation` rechecks the relation ID, the selected endpoint and the current graph scope. Origin, predicate direction, evidence status, proposal quote, source/revision, digest, quote offset and verifier verdict are shown without elevating a suggestion into confirmed evidence. Negative verifier verdicts are visible only with Suggestions enabled and explicitly labeled contradicted. Other visible observations between the same entities remain separate, with no automatic contradiction inference.

**Open current source** first revalidates the relation and requests a digest-bound current `preview`; only then can the matching active Library item/revision/digest navigate to Library. Otherwise the graph shows a bounded extracted-text window only when the exact quote occurs at its recorded UTF-16 offset. The source path is not treated as a PDF page, transaction row or timestamp; unsupported or absent exact anchors are reported, not guessed.

For an eligible proposal, **Accept** and **Reject** reuse the existing `topicReview` request and server eligibility check; rejection requires confirmation and reprojects the graph. While an edge is inspected, the existing workbench heartbeat rechecks its current visibility on a bounded cadence and hides revoked or changed evidence; it does not promise instantaneous invalidation. Owner-authored assertions have no source citation or proposal review buttons. The subject and object inspector shortcuts open the canonical Entities workflow for existing name/type/relationship owner decisions; they do not invent an owner-edge deletion action. **Ask Praxis** prepares a current relation-ID-based draft and switches to Chat; the owner must review the text, selected backend and disclosure policy before sending.

**Verification on the owner Mac:** complete locked .NET restore/build/test/format, Release Xcode build, app-hosted Swift tests, screenshots, plus a real graph session checking accepted and suggested relations, explicit unsupported verdict, multiple observations, source revision and revocation during inspection, rejected proposal removal, denied source preview, no available Chat backend, user-chosen Chat send, compact window and VoiceOver. Issue #388 owns quantified device, resource, security and accessibility qualification.

## Known delivery boundary

Both styles share the scoped containment projection. Entity types remain an optional facet. Browse and the evidence inspector preserve all currently visible canonical entities and relationships, including those that have no region placement. Follow the [classification audit](one-brain-classification-audit.md) before changing real classifications or approving the map.

This is a substantial visual redesign toward the four owner-provided constellation/Obsidian graph references, **not yet a validated full-corpus hierarchical LOD or configurable saved-lens implementation**. #420 retains global density/aggregation and navigation quality acceptance, #388 retains measured performance/accessibility/security acceptance. No arbitrary file co-occurrence edges, missing evidence anchors or project membership is invented.


## Knowledge Cosmos presentation mode — #420

The **Knowledge Cosmos** preference selects a perspective-projected, orbitable Metal cloud using deterministic positions for the same **region IDs** displayed by Cellular Atlas, with a bounded spatial lift of real scoped nodes at deeper zoom. The Metal canvas remains mounted as the camera eases between overall and focused views; loading authorized detail is still asynchronous. A type or a source is not treated as a semantic community.

MetalKit batches the root regions as points and renders regional real relations from the scoped `graphRegion`/`graphNeighborhood` read. The Whole Brain currently has **no links between root regions** by design until the catalog supplies an explicit scoped root-link projection; this is not a missing inferred-connection algorithm. The shell lights and up to 72 density motes per region are decorative, not entities or assertions. At detailed levels, confirmed, suggested and contradicted relation observations retain their status and exact IDs; accessible evidence remains in the SwiftUI inspector. Rendering budgets remain bounded and do not imply full-corpus coverage or a measured performance win.

Knowledge Cosmos supports mouse drag to orbit, Option/right-drag to pan, wheel/trackpad magnify, a rotate gesture, click-to-enter zone, keyboard Escape to return, and the existing toolbar zoom controls. The original Cellular Atlas and accessible SwiftUI context/relationship lists remain intact; the Metal view is deliberately not exposed as a separate VoiceOver fact navigator. Reduce Motion disables continuous redraw and snaps transitions to their targets; High Contrast increases point/link visibility. A Metal-unavailable renderer presents an explicit failure label rather than fabricating content.

**Owner-Mac acceptance still required:** compile and run the app and Xcode tests (including `.metal` shader compilation); compare both styles in the same real authorized corpus; examine camera movement with slow/fast scrolling and repeated enter/back, selection/inspection, light/dark and small windows, source revocation/filter invalidation, Reduce Motion and VoiceOver parity; profile p95/p99 frame time, GPU/CPU work, allocations, shader/pipeline startup, sustained orbit and regional transition latency. The first Metal implementation is a **testable mode, not a demonstrated performance win**. It is not an anatomical brain mesh, adaptive spatially tiled multilevel community graph, or a final HDR/postprocessed rendering engine. Those remain #420/#428/#388 gates.

## Shared concept regions — #455

`graphConceptRegions` returns roots or the immediate child groups of a supplied `parentRegionId`. It derives both from currently eligible containment observations. The scopes, cursor and evidence rules are documented in the [graph contract](../architecture/knowledge-graph-query.md). The existing `neuralNebula` preference still selects Knowledge Cosmos.

At the same source/collection scope, switch between styles at the root and while focused. Verify both show the same IDs, counts and subregions, and that Back, open evidence, optional type lens and Refresh work. Repeat after an accepted/rejected relationship, scope change and source revocation on a permitted scratch catalog. Include one real project with a mislabeled component and an unrelated concept, an empty scope and a scope with entities but no structural evidence. Check that an unrelated concept can form a region only with its own eligible structure.

The native view reads on entry, navigation, scope change and explicit refresh. Push invalidation, topic evidence proposals, links between regions, stable transitions through reparent/split/merge, and owner-corpus performance and visual acceptance remain open under #455. Living Cortex (#456) should consume the same projection once those gates pass.

## Ranked region coverage — #420 follow-up

The \`graphRegion\` desktop read requires an allowed \`entityType\` and enforces the same source, collection, entity visibility, relation status and suggestion-opt-in rules as the authoritative atlas and relation inspector. It considers the complete currently eligible relation set touching that type and derives the **per-endpoint degree within that current scoped subset**, not a stored or global popularity number. Each relation is attributed deterministically to its higher-degree endpoint (ties by endpoint ID). Pages are ordered by the *first eligible observation from each hub*, then the second, and so forth; higher-degree hubs lead within the same depth. Pending and contradicted links remain explicitly separate and are deprioritized **within their hub**, never silently promoted into confirmed relations. The original \`graphAtlas\` keyset endpoint is retained for callers that require ID-order traversal.

This ranking fixes the failure mode where the first 600 UUID-ordered relations could all come from one arbitrary component while other visible hubs went undiscovered. It is a **representative bounded sample**, not guaranteed complete coverage of every connected component or a detected community. The UI compares rendered relation observations with the authoritative total of eligible scoped relations touching that type from \`graphZones\`, so it cannot imply that the sample is exhaustive.

The private transport still enforces at most **100 relations per page** and the regional reader enforces a hard maximum of **600 edges per traversal**, including non-divisor page sizes. A session- and scope-bound continuation cursor checks the live catalog change counter before each page; any change, mismatched filter or malformed cursor fails closed rather than mixing snapshots. The query computes degree and round-robin rank over currently eligible records, so it may be more expensive than ID-order pagination on large corpora; **no SQL throughput/latency claim is made pending owner-Mac profiling**. Measure query plan, p95 query latency, temporary memory and cancellation responsiveness under dense high-degree and mixed-scope workloads before raising these budgets or declaring resource acceptance. No second graph store or persisted inferred links were added.

## Semantic zoom delivery checkpoint — #420

The first functional LOD slice provides **whole catalog aggregate type zones → bounded region → focused neighborhood → evidence inspector**, all under the original canonical graph authority and inside the same SpriteKit scene. Aggregating by recorded type is deliberately conservative; it does **not** yet deliver validated automatic graph communities, arbitrary hierarchical subzones, viewport tile paging, persistent view presets, GPU instancing or a guaranteed 60-FPS render. It is not the final visual fidelity milestone.

New synthetic captures `brain-zones-synthetic.png` and its dark variant show the aggregate level independently of private user data. Compare both levels using a real permitted corpus before accepting visual fidelity. Measure the whole-brain SQL response time, initial scene presentation, level switching, p95/p99 frame time, memory, and responsiveness across idle, drag, scroll, rotation, repeated enter/back and filter/revocation scenarios. Verify count integrity under mixed confirmed/pending/contradicted relations and verify VoiceOver zone navigation from the sidebar. Check the Xcode test deployment-target warnings.

**Do not merge solely on passing .NET/Swift tests or static screenshots.** Record owner-Mac build/test results, measured before/after traces and interactive design acceptance in the PR; #388 is an independent security, accessibility and resource gate.
