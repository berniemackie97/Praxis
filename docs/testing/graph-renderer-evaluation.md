# Knowledge graph renderer evaluation — #384

**Status:** Research and repeatable fixture generation complete. Native renderer benchmarks and the final technology decision are pending. Do not close #384 from this document alone.

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

The draft PR includes a small macOS-only comparison host under `scripts/macos/GraphRendererBenchmark`. It reads a **generated JSON fixture only**. It is not part of the production app, does not launch the .NET worker, and does not open an owner's Praxis data directory.

Run from the repository root on a Mac with full Xcode selected:

```sh
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/praxis-graph-384.XXXXXX")"
python3 scripts/macos/graph_renderer_fixtures.py "$fixture_dir"
bash scripts/macos/GraphRendererBenchmark/build-graph-benchmark.sh
.artifacts/graph-renderer-benchmark "$fixture_dir/connected.json"
```

Use the in-window renderer selector to compare Canvas and SpriteKit with the same record set. Restart the command with `sparse.json`, `clustered.json` and `high_degree.json` to inspect the other cases. Keep the test window size, display mode and motion toggle the same. Use a clean process for each profiling run. macOS Reduce Motion must stop the motion in both modes. The right-side SwiftUI list provides a separate keyboard and VoiceOver navigation path over the full node set.

**Limits of this initial prototype:** Both implementations redraw synthetic moving positions; this **does not implement a force-directed solver** and cannot measure real query or layout cost. Canvas currently includes an initial pointer-select, pan and zoom interaction; SpriteKit includes node selection but does not yet have equivalent pan, zoom and drag behavior. Therefore, this build supports an early **drawing and animation comparison**, not a complete interaction or accessibility decision. Do not record interaction parity as passed.

The fixture and tests ran under Python. The Swift source passed a syntax parse on a non-macOS host, but **the native Swift 6/macOS 13 build, UI tests and profiling have not run**. Do not interpret syntax validation as a successful Xcode build. Fix any compile errors on the draft branch before collecting measurements.

## Same-task comparison

Use a disposable host inside the existing Xcode application test target or a short-lived isolated experiment. Compare at least two runnable candidates. Include the third if either of the first two fails a required behavior or if measured results make the choice unclear.

1. Use the same case, graph viewport, text size, visible node/edge count, starting positions and display scale. Run Release-optimized code where practical and document configuration.
2. Compare idle, animated settling, pointer drag, pan, zoom, hit selection, focused neighbor highlighting, resize, background/inactive state and returning to the foreground. Do not count layout preparation as draw performance without reporting it separately.
3. Run each case with normal motion and with macOS Reduce Motion enabled. Under Reduce Motion, provide a static readable view rather than a running force simulation. Pause updates when the scene is inactive.
4. Demonstrate keyboard selection, focus order and VoiceOver descriptions on a visible accessible list/table. The list must expose the same node and relationship IDs as the visual selection. Color alone cannot identify relation status or kind.
5. Use Xcode Instruments or equivalent native profiling to record frame times, memory use, CPU load, energy behavior, input latency and stalls. Distinguish time spent in graph layout, label rendering and drawing.
6. Repeat each test three times after a documented warm-up. Record the machine, macOS version, Xcode version, power state, display refresh rate, window dimensions and fixture file digest. Report a range, not only the best run.

**Measurement guidance:** On a 60 Hz display, one refresh interval is about 16.7 ms. This is a reference for interpreting frame pacing, **not a measured result or a guaranteed pass threshold**. Record 50th/95th percentile frame time and observed missed frames. Add a justified performance budget to the final decision after the native baseline is measured. If dense content does not meet it, test focused views or visible-node caps before escalating rendering complexity.

## Decision record evidence required

| Test | Canvas | SpriteKit | WKWebView |
| --- | --- | --- | --- |
| Native build and macOS 13 API compatibility | Not run | Not run | Not run |
| Sparse / connected frame-time distribution | Not measured | Not measured | Not measured |
| Clustered / high-degree CPU and memory | Not measured | Not measured | Not measured |
| Drag, pan, zoom and selection quality | Not tested | Not tested | Not tested |
| Reduced motion and inactive-state pause | Not tested | Not tested | Not tested |
| Keyboard and VoiceOver path | Not tested | Not tested | Not tested |
| Offline bundle / security review | Built-in API only | Built-in API only | Native WebKit; JS contents unselected |

The **provisional candidate to prototype first is SpriteKit**, because its 2D scene and pause support align with a force-directed graph. That is an engineering hypothesis, not a selected technology. Test Canvas as a simple native comparison. Keep WebKit in scope if the measured interaction and accessibility costs justify it.

After measurements, write a concise accepted ADR with the selected renderer, rejected alternatives, limits, accessibility design and exact #386 boundaries. Then update the #384 acceptance checklist. Until that evidence exists, #384 remains open and continues to block #386.
