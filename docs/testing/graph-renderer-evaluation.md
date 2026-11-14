# Knowledge graph renderer evaluation — #384

**Status:** Architecture decision completed in [ADR 0048](../decisions/0048-native-knowledge-graph-spritekit.md): use SpriteKit in the native SwiftUI Knowledge workspace. This document preserves the earlier research, synthetic fixtures and owner Mac findings. The cross-renderer benchmark plan below is historical guidance, **not an outstanding prerequisite**. Production performance and accessibility acceptance remain mandatory under #386 and #388.

## Existing application contract

- The only first-party macOS application is the Xcode-owned Swift 6 target in `apps/macos/Praxis.xcodeproj`, with deployment target macOS 13.0. Keep the existing SwiftUI `NavigationSplitView` and Knowledge workspace. Do not introduce another production app or worker.
- `KnowledgeWorkbenchModel` already reads entities and detail through `WorkerChannel`. The graph read contract belongs to #385, not this spike. All benchmark content must be generated synthetic data; no owner catalog, model, permission grant or network endpoint.
- The graph surface displays an authorized, bounded projection. Node positions, clustering and animation do not create facts or change provenance. The renderer must not write source data.
- The existing app-hosted Swift tests and screenshots offer a test path for #386 after the renderer is selected. Do not use an uncontrolled preview against the owner's saved catalog.

## Candidate technologies

| Candidate | Supported basis | Unknowns to measure |
| --- | --- | --- |
| SwiftUI `Canvas` + `TimelineView` | Fits the existing SwiftUI hierarchy. Draws a whole graph in one surface. Apple states that Canvas has **no per-element accessibility or interactivity**. A separate accessible list and explicit hit testing are required. | Frame pacing under changing node positions; legible labels; drag/hover hit tests; CPU and memory use. |
| `SpriteKit` hosted by `SKView` | Apple-supplied 2D rendering and animation with a native macOS view. A paused scene can stop its update loop. Keep a SwiftUI accessible list, navigation and inspectors outside the scene. | Frame pacing, large-node memory, labels, coordinate transforms, event/focus handling and energy cost. |
| Offline `WKWebView` graph | Supports a bundled HTML/JavaScript graph with existing web layout or force-directed libraries. The application remains the authority and must limit the native/JS bridge. | Extra web-process cost, offline packaging, keyboard/VoiceOver behavior, revocation and stale-message handling. |

**Not a baseline choice:** A custom Metal rendering engine adds rendering and accessibility work before we have evidence that the higher-level options fail. A third-party graph package needs an explicit license and bundle audit. No CDN, runtime package fetch or unreviewed JavaScript bridge is permitted.

Apple documentation:
- [SwiftUI Canvas](https://developer.apple.com/documentation/swiftui/canvas)
- [SwiftUI TimelineView](https://developer.apple.com/documentation/swiftui/timelineview)
- [SpriteKit](https://developer.apple.com/documentation/spritekit)
- [SKView](https://developer.apple.com/documentation/spritekit/skview)
- [WKWebView](https://developer.apple.com/documentation/webkit/wkwebview)
- [Reduce Motion](https://developer.apple.com/documentation/swiftui/environmentvalues/accessibilityreducemotion)

## Reproducible synthetic cases

Run from the repository root:

```sh
python3 -m unittest discover -s scripts/macos -p 'test_graph_renderer_fixtures.py' -v
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/praxis-graph-384.XXXXXX")"
python3 scripts/macos/graph_renderer_fixtures.py "$fixture_dir"
```

The generator writes four fixed JSON cases. It refuses to overwrite existing files. All candidates must use the same node and edge records, starting coordinates, status labels and viewport size.

| Case | Nodes | Edges | Max degree | Evaluation purpose |
| --- | ---: | ---: | ---: | --- |
| Sparse | 32 | 12 | 1 | Empty areas, disconnected nodes, keyboard navigation |
| Connected | 200 | 400 | 4 | Typical neighborhood exploration and interaction |
| Clustered | 800 | 1,607 | 6 | Labels, focus and clustered layout |
| High degree | 1,500 | 2,697 | 1,199 | Fan-out stress, overlap and bounded display |

These are **synthetic load cases**, not a proposed catalog-wide render budget. No claim about real user graph sizes is made. The `seedX`/`seedY` fields are initial display positions only, not evidence or persisted layout. The generator is platform-neutral so it can be checked before a macOS graphical test.

## Disposable native rendering prototypes

The renderer spike includes a small macOS-only comparison host under `scripts/macos/GraphRendererBenchmark`. It reads a **generated JSON fixture only**. It is not part of the production app, does not launch the .NET worker, and does not open an owner's Praxis data directory.

Run from the repository root on a Mac with full Xcode selected:

```sh
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/praxis-graph-384.XXXXXX")"
python3 scripts/macos/graph_renderer_fixtures.py "$fixture_dir"
bash scripts/macos/GraphRendererBenchmark/build-graph-benchmark.sh
open -n -a "$PWD/.artifacts/GraphRendererBenchmark.app" --args "$fixture_dir/connected.json"
```

The benchmark is packaged as a separate macOS app and started through Launch Services rather than directly executing a SwiftUI binary. This resolves a suspected cause of the owner's first build launching without a visible window; the correction still needs owner-Mac runtime verification. The `open` command returns to the terminal. If the window does not appear, inspect macOS application logs and process state rather than assuming it is running.

Use the in-window renderer selector to compare Canvas and SpriteKit with the same record set. Restart the command with `sparse.json`, `clustered.json` and `high_degree.json` to inspect the other cases. Keep the test window size, display mode and motion toggle the same. Use a clean process for each profiling run. macOS Reduce Motion must stop the motion in both modes. The right-side SwiftUI list provides a separate keyboard and VoiceOver navigation path over the full node set.

**Limits of this initial prototype:** Both implementations redraw synthetic moving positions; this **does not implement a force-directed solver** and cannot measure real query or layout cost. Canvas currently includes an initial pointer-select, pan and zoom interaction; SpriteKit includes node selection but does not yet have equivalent pan, zoom and drag behavior. Therefore, this build supports an early **drawing and animation comparison**, not a complete interaction or accessibility decision. Do not record interaction parity as passed.

The owner ran the fixture tests (2 passed) and compiled the initial unbundled Swift executable successfully on macOS. Four SpriteKit actor-isolation warnings were observed, and no visible window was confirmed. A follow-up package/actor correction is in PR #395. **A successful build is not a confirmed launched window or a measured renderer result.** Run the bundled app, verify visible controls and collect measurements before closing #384.

## Owner-Mac SpriteKit crash — November 13, 2026

The owner's new macOS application bundle compiled without the earlier actor-isolation warnings and launched a visible graph window. Selecting SpriteKit caused a crash on macOS 26.6.2, Apple Silicon (crash report dated 2026-11-13 21:44:43 -0500).

The stack traces `GraphScene.init(fixture:)` → SpriteKit `-[SKScene initWithSize:]` → `didChangeSize` → `updatePositions(at:)`, ending in a main-thread Swift trap. SpriteKit invokes the size-change override while the superclass initializer is still executing. At that time the benchmark has not appended its circle nodes. The first `circles[index]` access is therefore invalid.

The regression fix makes `didChangeSize` skip the premature layout until the number of circle nodes equals the fixture node count. Initial layout still happens at the end of the scene initializer; subsequent real size changes still update the scene.

**Reproduce and verify on owner macOS:**

```sh
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/praxis-graph-384.XXXXXX")"
python3 scripts/macos/graph_renderer_fixtures.py "$fixture_dir"
bash scripts/macos/GraphRendererBenchmark/test-spritekit-startup.sh "$fixture_dir"/*.json
bash scripts/macos/GraphRendererBenchmark/build-graph-benchmark.sh
open -n -a "$PWD/.artifacts/GraphRendererBenchmark.app" --args "$fixture_dir/sparse.json"
```

The new native smoke test creates and resizes a SpriteKit scene for each synthetic graph and checks its child count. It is **not yet executed on the owner's Mac**. After it passes, also select SpriteKit in the graphical comparison app and test the graph and window resizing; the smoke test does not cover the full SwiftUI integration. Leave #384 open until this regression and the renderer selection acceptance both pass.

## Historical comparison plan (not a delivery requirement)

Use a disposable host inside the existing Xcode application test target or a short-lived isolated experiment. Compare at least two runnable candidates. Include the third if either of the first two fails a required behavior or if measured results make the choice unclear.

1. Use the same case, graph viewport, text size, visible node/edge count, starting positions and display scale. Run Release-optimized code where practical and document configuration.
2. Compare idle, animated settling, pointer drag, pan, zoom, hit selection, focused neighbor highlighting, resize, background/inactive state and returning to the foreground. Do not count layout preparation as draw performance without reporting it separately.
3. Run each case with normal motion and with macOS Reduce Motion enabled. Under Reduce Motion, provide a static readable view rather than a running force simulation. Pause updates when the scene is inactive.
4. Demonstrate keyboard selection, focus order and VoiceOver descriptions on a visible accessible list/table. The list must expose the same node and relationship IDs as the visual selection. Color alone cannot identify relation status or kind.
5. Use Xcode Instruments or equivalent native profiling to record frame times, memory use, CPU load, energy behavior, input latency and stalls. Distinguish time spent in graph layout, label rendering and drawing.
6. Repeat each test three times after a documented warm-up. Record the machine, macOS version, Xcode version, power state, display refresh rate, window dimensions and fixture file digest. Report a range, not only the best run.

**Measurement guidance:** On a 60 Hz display, one refresh interval is about 16.7 ms. This is a reference for interpreting frame pacing, **not a measured result or a guaranteed pass threshold**. Record 50th/95th percentile frame time and observed missed frames. Add a justified performance budget to the final decision after the native baseline is measured. If dense content does not meet it, test focused views or visible-node caps before escalating rendering complexity.

## Historical comparison checklist (unmeasured)

| Test | Canvas | SpriteKit | WKWebView |
| --- | --- | --- | --- |
| Native build and macOS 13 API compatibility | Not run | Not run | Not run |
| Sparse / connected frame-time distribution | Not measured | Not measured | Not measured |
| Clustered / high-degree CPU and memory | Not measured | Not measured | Not measured |
| Drag, pan, zoom and selection quality | Not tested | Not tested | Not tested |
| Reduced motion and inactive-state pause | Not tested | Not tested | Not tested |
| Keyboard and VoiceOver path | Not tested | Not tested | Not tested |
| Offline bundle / security review | Built-in API only | Built-in API only | Native WebKit; JS contents unselected |

## Decision and remaining acceptance

[ADR 0048](../decisions/0048-native-knowledge-graph-spritekit.md) selects **SpriteKit** for native graph rendering, with SwiftUI for the surrounding interface and accessible evidence navigation. The selection is grounded in supported framework behavior and Praxis architecture, not an asserted performance ranking. The owner's 200/800-node demonstrations ran smoothly but their visual design was poor; the comparison did not control presentation or produce profiling measurements.

This is a decision spike, not a release-qualification exercise. Do **not** build a duplicate Canvas renderer or a WKWebView prototype simply to create a comparison. The reproduction fixtures and smoke tests remain useful for #386. Implement read-only, bounded scene updates and meaningful focus; then measure the **selected implementation** under #388. Frame pacing, CPU/memory/energy, accessibility, keyboard navigation, Reduce Motion and source revocation must still pass. Reopen the renderer decision only if that evidence identifies a concrete blocker.
