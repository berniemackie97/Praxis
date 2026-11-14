# ADR 0048: SpriteKit for the native knowledge graph scene

**Status:** Accepted architecture choice (owner-directed, November 13, 2026). Implementation and macOS quality acceptance remain open in #386 and #388.

## Context

Praxis is a native macOS SwiftUI application with a .NET worker that owns canonical knowledge, evidence, permissions, revisions and source revocation. The Knowledge workspace already lives in the existing `NavigationSplitView`. Feature #383 requires a readable, interactive, force-directed view of **authorized** entities and relationships. The renderer must provide smooth node selection, drag, pan, zoom and focus without inferring a relationship from proximity or creating another graph store.

Issue #384 examined SwiftUI `Canvas`/`TimelineView`, native `SpriteKit` and bundled `WKWebView`. The owner prefers a decision based on API capabilities and the actual Praxis requirements rather than further aesthetic comparisons of unequal prototypes.

## Decision

Use **SpriteKit** (`SKView`/`SKScene`) for the graph's interactive 2D drawing and scene interaction, hosted inside the **existing SwiftUI Knowledge workspace** through `NSViewRepresentable`.

Keep **SwiftUI** as the authority for app navigation, search and filters, selection-dependent evidence inspectors, status and errors, keyboard/VoiceOver-compatible list or table navigation, and the shared design system. These surfaces must use the same authorized entity and relationship identifiers as the visual scene.

This is an **interface technology decision**, not an instruction to use SpriteKit physics as a semantic graph engine. A graph layout algorithm is separate and must operate only on the bounded, immutable-for-rendering projection supplied by #385. No custom global rendering framework, second data store, remote visualization service, or runtime-downloaded graph library.

## Basis and limits of evidence

- Apple's SpriteKit documentation defines a hierarchical 2D node scene, an animation/update loop, `nodes(at:)` and `atPoint(_:) ` hit testing, and scene pause/rate controls. These directly support the intended graph interactions. `NSViewRepresentable` is Apple's supported integration boundary for an AppKit view in SwiftUI.
- Apple's SwiftUI documentation states that `Canvas` can draw dynamic 2D graphics but **does not provide individual-element accessibility or interactivity**. Those must be implemented outside the drawing operations, alongside custom hit testing.
- Apple's WebKit documentation establishes that `WKWebView` embeds HTML, CSS and JavaScript content. A local offline web graph could meet the visual requirements, but adds a native/web coordination and packaging boundary with no demonstrated need for it.
- The owner ran a native Apple Silicon benchmark on macOS 26.6.2. Synthetic sparse, connected (200 nodes), clustered (800 nodes), and high-degree scene initialization/resizing smoke tests passed after #397. The owner observed that **both Canvas and SpriteKit felt smooth** for the 200- and 800-node examples. This was a qualitative observation, not a frame-time/CPU/memory benchmark.
- The two prototypes had different visual treatments. Preference for the SpriteKit *prototype's appearance* is **not** evidence of renderer superiority. The dense examples looked poor because the current graph design did not control clutter, labels, or focus. They did not expose a proven renderer performance defect.
- The initial SpriteKit benchmark did crash when its resize callback ran during `SKScene` construction, before its node array was initialized. PR #397 fixed that initialization bug and added smoke tests. This is a benchmark implementation defect, not evidence that SpriteKit is inherently unreliable.

The selection is based on **API capability and architectural fit**, not a claimed speed ranking. There is no measured performance winner or validated production capacity yet.

## Rejected alternatives

**SwiftUI Canvas as the main interactive scene:** Keep it available for ordinary static drawing, if needed. Its immediate-mode approach is useful but would require us to build more per-node hit testing and interaction behavior for this graph. It offers no documented advantage that offsets that responsibility for the current requirements. Do not introduce parallel production renderers.

**WKWebView graph:** Local HTML/JavaScript could provide rich layouts. Praxis already has a native SwiftUI shell and can meet the interaction contract through built-in frameworks. Adopting WKWebView would require additional data synchronization, message validation, offline packaging, dependency and accessibility review with no present product requirement that justifies this boundary.

**Custom Metal renderer:** Reserve it for a demonstrated constraint after profiling and smaller, documented optimization options. No evidence currently requires bespoke GPU shaders or a rendering engine.

## Implementation boundaries for #385 / #386

1. **Canonical data and access control:** The .NET worker remains the sole authority for entity, relation, revision, evidence and visibility. #385 provides bounded, permission-filtered, read-only neighborhoods. The renderer never directly queries SQLite or caches unauthorized identifiers, counts or previews.
2. **Stable projection, temporary positions:** Keep a small model of authorized node/edge IDs and labels for the current viewport. User drag, simulation position and temporary focus are presentation state only. If pinned positions are later saved, they are preferences, not asserted relationships or source data.
3. **Meaningful focus, not all-at-once clouds:** Start with a selected entity/neighborhood; expand only on demand. Use semantic zoom, restrained label density, visible relation direction/status and stable selection. Do not turn proximity into a predicate, and do not default to drawing 800 labeled nodes.
4. **Bounded work:** Enforce query caps and cancellation in #385; enforce visible-node/edge, simulation-work, label and scene-resource caps in #386. Choose quantitative budgets from measured baseline rather than inventing a hardware threshold in this ADR. Stop unnecessary updates when settled, inactive or offscreen. Apple notes that even automatic SpriteKit culling visits scene nodes; remove unused nodes from the scene when practical.
5. **Interaction:** Coordinate transforms, hit testing, drag, pan, zoom, selection and keyboard navigation require tests. Use one selected-ID state shared with SwiftUI inspector/list. A visual edge should open only the actual eligible evidence and review action.
6. **Accessibility:** The graph is not the only navigation path. Provide a usable SwiftUI list/table and equivalent selection and evidence actions, including VoiceOver and full keyboard support. Honor high contrast and `accessibilityReduceMotion`; no decorative simulation while motion reduction is enabled.
7. **Lifecycle:** Pause scene updates on loss of activity, idle/settled layout, view removal and background state; cancel obsolete neighborhood work. Rebuild the visible scene from current authorized data after revocation or revision rather than displaying stale connections.
8. **Packaging:** Built-in macOS frameworks only; no network asset fetch or third-party license burden from the renderer choice. Keep the production target under the current macOS 13 minimum, Swift 6 and Xcode-owned app.

## Verification gates and follow-up

**#384 decision-spike exit:** This ADR selects the renderer on supported API behavior, the audited native architecture and the owner Mac feasibility result. A new aesthetic comparison and a performance competition between the two discarded candidates are **not** prerequisites to this architecture choice.

**#386 implementation exit:** Prove the real interaction, local layout, readable focus, accessibility alternative, Reduce Motion, safe lifecycle and bounded resource handling with hosted native tests and representative synthetic graph examples.

**#388 owner-hardware acceptance:** Measure frame-time distributions, memory, CPU, energy, interaction latency and failure behavior on the *production* SpriteKit graph over bounded, authorized data. Include sparse/clustered/high-degree data, dense readability, keyboard and VoiceOver paths, reduced-motion and source-revocation tests. Set explicit production limits from evidence, adjust the design or optimize only if measurements justify it. This gate remains mandatory even though we do not compare multiple renderers again.

**Fallback policy:** If #388 demonstrates a blocker that cannot be corrected within reasonable implementation complexity, reopen this ADR with specific evidence and reconsider Canvas, WKWebView or Metal against the failed requirement. Do not maintain multiple renderer implementations speculatively.

## References

- [Apple: SpriteKit node scene](https://developer.apple.com/documentation/spritekit/getting-started-with-nodes)
- [Apple: hit testing](https://developer.apple.com/documentation/spritekit/understanding-hit-testing)
- [Apple: SKView pause, rendering and FPS](https://developer.apple.com/documentation/spritekit/skview)
- [Apple: embedding AppKit in SwiftUI](https://developer.apple.com/documentation/swiftui/nsviewrepresentable)
- [Apple: Canvas limitations](https://developer.apple.com/documentation/swiftui/canvas)
- [Apple: WKWebView](https://developer.apple.com/documentation/webkit/wkwebview)
- [Apple: Reduce Motion](https://developer.apple.com/documentation/swiftui/environmentvalues/accessibilityreducemotion)
- [Apple: SpriteKit node culling tradeoffs](https://developer.apple.com/documentation/spritekit/maximizing-node-drawing-performance)
- [Issue #384](https://github.com/berniemackie97/Praxis/issues/384) and [renderer fixtures / smoke test](../../scripts/macos/GraphRendererBenchmark/)
