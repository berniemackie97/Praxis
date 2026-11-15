# Native Knowledge Graph — #386

## Product boundary

The Knowledge workspace offers **Browse / Connections** views of the **same** catalog. The existing list/detail and verification views stay in place. The Graph view embeds a first-party SpriteKit scene inside SwiftUI; SwiftUI provides the seed search, collection/source/type/relation/evidence filters, bounded expansion controls and keyboard/VoiceOver entity/relationship list.

The graph reads the private desktop worker's `graphZones`, scoped `graphAtlas`, `graphSeeds`, `graphNeighborhood` and `graphRelation` responses. The .NET catalog remains the authority for entity identity, source visibility, revisions, review state, relation predicates and direction. The scene does not write the catalog, infer relationships from proximity, contact a model, or read user files directly. #387 adds current relationship reinspection, guarded owner review and supported source-text navigation. Unsupported media or PDF anchors remain explicitly unavailable.

## Interaction

1. Open **Knowledge → Brain**. The main surface groups **all currently permitted entities by recorded entity type** into large knowledge zones. Real relation-observation counts between types form aggregate links; type membership does **not** imply that every member is connected, and zero-link zones remain visible. It never auto-focuses the first ID-sorted seed or depends on a 600-edge UUID-order sample to compose the Whole Brain. The optional Entities view and left context rail remain secondary navigators.
2. Click a zone or zoom inward over it to load its bounded current entity-and-edge region. Zoom outward or choose **Whole Brain** to return to the aggregate view. **Focus** opens a bounded one-hop neighborhood from an actual entity, and **Back to region** returns to the selected type lens. Search still queries authorized current entity IDs: matching entities enter their type zone, recenter when present in the bounded region, or enter an evidence-backed focused neighborhood when outside the sample. The inspector and source/review controls remain unchanged.
3. Drag to pan either level, wheel/pinch to zoom around the pointer, and use a two-finger rotate gesture or Option-drag to rotate the atlas. **Fit** resets scale/orientation for the active level. Focused-neighborhood nodes may be dragged individually. The toolbar zoom steps remain relative to the current camera. Positions and orientation are presentation-only.
4. Click an edge or select it from the SwiftUI list to inspect predicate, direction, trust status and exact available source excerpt. An owner assertion explicitly has **no invented citation**. Inactive or denied selections are removed when the projection is refreshed.
5. Filter by active collection, active source, entity type, predicate and evidence status. **Suggestions** is an explicit opt-in; a suggestion is never labeled confirmed. A filter change invalidates outstanding replies and clears the old projection before requerying.
6. Use **Refresh** after intake, approval or revocation changes. Leaving the graph clears the visible projection and detaches/pauses SpriteKit. No graph worker requests continue while that surface is inactive.

## Presentation constraints

- The Whole Brain `graphZones` projection aggregates all currently scoped and eligible entity types and relation observations in SQLite into a bounded type-pair result, with **no raw atlas edge hydration** at the overview level. A type zone is a group, not a connected component or semantic inference. Clicking or zooming into a type uses existing `graphAtlas` pages for up to **600 real relations / 960 endpoints** plus up to 32 browse/search entities; it is explicitly labeled as a *bounded regional sample*, not the entire type. Focused neighborhoods retain their separate **120-node / 120-edge** cap, 32 seed results and 60-edge pages. These are provisional budgets, not claimed device maxima.
- Nodes are colored by type, and proposed names are muted. Selection and evidence statuses are also expressed in text. Labels are shown for small graphs and focused/selected nodes, and expanded at closer zoom; dense neighborhoods do not label everything at once.
- The zone renderer constructs one visual hub per permitted type with deterministic, bounded weighted relaxation driven only by actual aggregated links. Its counted inter-zone strokes distinguish confirmed, suggested and contradicted observations. Region detail uses the existing batched SpriteKit atlas and its real connected-component layout; focused neighborhoods retain capped 180-step simulation. Animated zones are visual state, never a claim of novel edges. No link is inferred from proximity, and neither GPU throughput nor full-corpus hierarchy is claimed before measured qualification.
- The scene is a visual surface, hidden from VoiceOver. The SwiftUI list and evidence panel expose the same currently authorized entity and relation identifiers via ordinary buttons, without requiring pointer input.
- macOS Reduce Motion disables the focused simulation and pauses the bounded global hub halo animation; increased contrast changes line and outline visibility. The screenshot fixture also exercises light/dark and compact window sizes.
- The renderer uses bundled Apple frameworks (SpriteKit and SwiftUI), no remote assets or graph engine package. It remains inside the existing `NavigationSplitView`.

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

The zone entry replaces the flat 600-edge sample as the global representation. It shows all current scoped entity type counts and actual inter-type relation-observation counts; the bounded expanded region still does **not** render every entity in a large corpus. The authoritative count comes from `graphOverview`, and the view shows current rendered counts separately. The left contexts are filters on that same graph, not separate brains. Browse remains a secondary entity inspector (up to 200 most-mentioned rows grouped by type), with preserved review IDs and progressively disclosed evidence. Data extraction quality is separately measured under #315.

This is a substantial visual redesign toward the four owner-provided constellation/Obsidian graph references, **not yet a validated full-corpus hierarchical LOD or configurable saved-lens implementation**. #420 retains global density/aggregation and navigation quality acceptance, #388 retains measured performance/accessibility/security acceptance. No arbitrary file co-occurrence edges, missing evidence anchors or project membership is invented.


## Semantic zoom delivery checkpoint — #420

The first functional LOD slice provides **whole catalog aggregate type zones → bounded region → focused neighborhood → evidence inspector**, all under the original canonical graph authority and inside the same SpriteKit scene. Aggregating by recorded type is deliberately conservative; it does **not** yet deliver validated automatic graph communities, arbitrary hierarchical subzones, viewport tile paging, persistent view presets, GPU instancing or a guaranteed 60-FPS render. It is not the final visual fidelity milestone.

New synthetic captures `brain-zones-synthetic.png` and its dark variant show the aggregate level independently of private user data. Compare both levels using a real permitted corpus before accepting visual fidelity. Measure the whole-brain SQL response time, initial scene presentation, level switching, p95/p99 frame time, memory, and responsiveness across idle, drag, scroll, rotation, repeated enter/back and filter/revocation scenarios. Verify count integrity under mixed confirmed/pending/contradicted relations and verify VoiceOver zone navigation from the sidebar. Check the Xcode test deployment-target warnings.

**Do not merge solely on passing .NET/Swift tests or static screenshots.** Record owner-Mac build/test results, measured before/after traces and interactive design acceptance in the PR; #388 is an independent security, accessibility and resource gate.
