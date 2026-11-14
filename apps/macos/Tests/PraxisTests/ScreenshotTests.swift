import AppKit
import Foundation
import SwiftUI
import Testing
@testable import Praxis

/// Renders every workspace against a real worker and a small fixture library, in light and
/// dark appearance, for visual review. Runs only when `PRAXIS_SNAPSHOT_DIR` names an output
/// folder (scripts/macos/capture-screenshots.sh sets it).
@MainActor
@Suite("Workbench screenshots")
struct ScreenshotTests {
    @Test("Each workspace and settings tab renders")
    func capture() async throws {
        guard let output = ProcessInfo.processInfo.environment["PRAXIS_SNAPSHOT_DIR"],
              let dotnet = WorkerFixture.dotnetPath else { return }
        let files = FileManager.default
        let outputURL = URL(fileURLWithPath: output, isDirectory: true)
        try files.createDirectory(at: outputURL, withIntermediateDirectories: true)

        let fixture = files.temporaryDirectory.appendingPathComponent("praxis-screens-\(UUID().uuidString)")
        let inbox = fixture.appendingPathComponent("Inbox")
        try files.createDirectory(at: inbox, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        defer { try? files.removeItem(at: fixture) }
        for (name, text) in Self.samples {
            try text.write(to: inbox.appendingPathComponent(name), atomically: true, encoding: .utf8)
        }

        let setup = WorkbenchModel()
        try await render(SetupView(dataRoot: .constant(""), inbox: .constant(""), additionalInboxes: .constant("[]"),
                             rootApprovals: .constant("{}"), configured: .constant(false), dotnetExecutable: .constant(""))
                    .environmentObject(setup),
                   size: CGSize(width: 1100, height: 760), name: "setup", to: outputURL)

        let model = WorkbenchModel()
        let canonicalInbox = CanonicalPath.of(inbox.path)
        let approvals = WatchedFolderSettings.encode([canonicalInbox.path: WatchedFolderSettings.identity(canonicalInbox) ?? ""])
        model.start(dataRoot: fixture.appendingPathComponent("PraxisData").path, inbox: canonicalInbox.path,
                    additionalInboxes: "[]", rootApprovals: approvals, initialize: true, dotnetExecutable: dotnet)
        defer { model.stop() }
        #expect(model.running, "\(model.message)")
        guard model.running else { return }

        let deadline = ContinuousClock.now + .seconds(30)
        while model.items.count < Self.samples.count && ContinuousClock.now < deadline {
            model.refresh()
            try await Task.sleep(for: .milliseconds(300))
        }
        model.createCollection("Household")
        model.createCollection("Work projects")
        try await Task.sleep(for: .milliseconds(500))
        model.refresh()
        try await Task.sleep(for: .milliseconds(500))
        if let first = model.items.first(where: { $0.title.hasSuffix(".md") }) { model.select(first) }
        model.search.query = "invoice"
        model.search.submit()
        model.noteTitle = "Ideas for the garden"
        model.noteText = "- Raised beds along the south fence\n- Ask Sam about the irrigation timer\n"
        try await Task.sleep(for: .seconds(1))
        if let hit = model.search.hits.first { model.search.select(hit) }
        try await Task.sleep(for: .milliseconds(600))

        await settle(model)
        for section in AppSection.allCases {
            for dark in [false, true] {
                try await render(SectionHost(section: section).environmentObject(model),
                           size: CGSize(width: 1180, height: 760),
                           name: "\(section.rawValue)\(dark ? "-dark" : "")", dark: dark, to: outputURL)
            }
        }
        for dark in [false, true] {
            try await render(SyntheticGraphPreview(),
                             size: CGSize(width: 1100, height: 760),
                             name: "graph-synthetic\(dark ? "-dark" : "")", dark: dark, to: outputURL)
        }
        try await render(SyntheticGraphPreview(forceReducedMotion: true),
                         size: CGSize(width: 780, height: 540),
                         name: "graph-synthetic-compact-reduced-motion", to: outputURL)
        for tab in [SettingsTab.general, .models, .privacy] {
            await settle(model)
            UserDefaults.standard.set(tab.rawValue, forKey: SettingsTab.storageKey)
            try await render(SettingsView().environmentObject(model), size: CGSize(width: 760, height: 680),
                       name: "settings-\(tab.rawValue)", wait: 1.5, to: outputURL)
        }
        try await render(SectionHost(section: .library).environmentObject(model), size: CGSize(width: 780, height: 560),
                   name: "library-narrow", to: outputURL)
    }

    /// Waits until the worker has answered earlier requests, so captures show loaded data.
    private func settle(_ model: WorkbenchModel) async {
        let deadline = ContinuousClock.now + .seconds(20)
        while model.workerBacklogged && ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(200))
        }
    }

    private struct SectionHost: View {
        @State var section: AppSection
        var body: some View { WorkbenchSplitView(section: $section) }
    }

    private func render<Content: View>(_ view: Content, size: CGSize, name: String, dark: Bool = false,
                                       wait: Double = 1.0, to folder: URL) async throws {
        let window = NSWindow(contentRect: CGRect(origin: CGPoint(x: 80, y: 80), size: size),
                              styleMask: [.titled, .closable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        window.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: view)
        window.contentView = host
        // Behind other windows: capturing this process's own window needs no screen-recording access.
        window.orderBack(nil)
        // Yield the main actor (a nested run loop would block worker replies queued behind this test).
        try await Task.sleep(for: .seconds(wait))
        host.layoutSubtreeIfNeeded()
        window.display()
        guard let image = CGWindowListCreateImage(.null, .optionIncludingWindow, CGWindowID(window.windowNumber),
                                                  [.boundsIgnoreFraming, .bestResolution]) else { return }
        try NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?
            .write(to: folder.appendingPathComponent("\(name).png"))
        window.orderOut(nil)
        window.close()
    }

    private static let samples: [(String, String)] = [
        ("Garden plan.md", "# Garden plan\n\nRaised beds along the south fence. Sam recommended a drip irrigation timer from Green Thumb Supply.\n\n## Budget\n\nAbout $240 for lumber and soil.\n"),
        ("Contractor invoice.txt", "Invoice 2026-118 from Ortega Renovations\nKitchen cabinet installation\nTotal due: $3,480.00 by December 1, 2026\n"),
        ("Reading list.md", "# Reading list\n\n- Thinking in Systems\n- The Design of Everyday Things\n- How to Take Smart Notes\n"),
        ("Meeting notes - Praxis.md", "# Praxis sync\n\nDecided to ship the macOS redesign before the agent tools. Alex owns the intake ladder; Priya reviews the privacy settings.\n"),
        ("Groceries.csv", "Date,Description,Amount\n2026-10-02,Market Fresh,-84.12\n2026-10-09,Market Fresh,-61.40\n2026-10-15,Corner Bakery,-12.75\n"),
        ("Lisbon flights.eml", "From: Priya <priya@example.com>\r\nTo: owner@example.com\r\nSubject: Lisbon flights booked\r\nDate: Thu, 12 Nov 2026 09:30:00 -0800\r\n\r\nFlights are booked for March 3, 2026. Total EUR 412.50.\r\n"),
        ("Trip ideas.md", "# Trip ideas\n\nLisbon in spring. Ask Priya about the tram museum. Book flights before February for the invoice deadline.\n")
    ]
}

@MainActor
private struct SyntheticGraphPreview: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private let forceReducedMotion: Bool

    init(forceReducedMotion: Bool = false) {
        self.forceReducedMotion = forceReducedMotion
    }
    @Environment(\.colorScheme) private var scheme
    @Environment(\.colorSchemeContrast) private var contrast
    @State private var selectedNode: String? = "project"
    @State private var selectedEdge: String?

    private let nodes: [GraphNode] = [
        GraphNode(entityId: "project", label: "Praxis", entityType: "project", typeOrigin: "owner",
                  status: "confirmed", mentions: 2, sources: 2, typeConflict: false, evidence: nil,
                  actions: ["openEntity"]),
        GraphNode(entityId: "reader", label: "Document Reader", entityType: "technology", typeOrigin: "verified",
                  status: "confirmed", mentions: 1, sources: 1, typeConflict: false, evidence: nil,
                  actions: ["openEntity"]),
        GraphNode(entityId: "ledger", label: "Ledger", entityType: "concept", typeOrigin: "inferred",
                  status: "suggested", mentions: 1, sources: 1, typeConflict: false, evidence: nil,
                  actions: ["openEntity"])
    ]
    private let edges = [
        GraphEdge(relationId: "e1", subjectId: "project", objectId: "reader", predicate: "uses",
                  origin: "owner", evidenceStatus: "owner", evidence: nil, actions: ["inspectRelation"]),
        GraphEdge(relationId: "e2", subjectId: "reader", objectId: "ledger", predicate: "produces",
                  origin: "model", evidenceStatus: "suggested", evidence: nil, actions: ["inspectRelation"])
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            PageHeader(title: "Knowledge Graph", subtitle: "Synthetic fixture; no owner sources loaded")
            GraphSceneView(nodes: nodes, edges: edges, focusId: "project",
                           selectedNodeId: $selectedNode, selectedEdgeId: $selectedEdge,
                           reduceMotion: reduceMotion || forceReducedMotion, active: true,
                           highContrast: contrast == .increased,
                           colorScheme: scheme, zoom: 1, zoomRevision: 0)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding(Spacing.l)
    }
}
