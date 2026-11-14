import SwiftUI

/// Explore current evidence in the existing workspace; the scene never performs catalog writes.
struct KnowledgeGraphView: View {
    @ObservedObject var graph: GraphWorkbenchModel
    let collections: [SourceCollection]
    let sources: [LibraryItem]
    let inspect: (String) -> Void
    let explain: (String) -> Void
    let openSource: (GraphEvidence) -> Bool

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.colorScheme) private var scheme
    @State private var zoom: CGFloat = 1
    @State private var zoomRevision = 0
    @State private var confirmRejection = false

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            HStack(spacing: 0) {
                discovery
                    .frame(width: 250)
                Divider()
                ZStack {
                    if graph.nodes.isEmpty {
                        EmptyState(systemImage: "point.3.connected.trianglepath.dotted",
                                   title: graph.loading ? "Loading connections" : "No visible connections",
                                   message: graph.outcome == "unavailable"
                                       ? "The selected entity is no longer available in this scope."
                                       : "Search or choose a confirmed entity to explore its relationships.")
                    } else {
                        GraphSceneView(nodes: graph.nodes, edges: graph.edges, focusId: graph.focusId,
                                       selectedNodeId: $graph.selectedNodeId, selectedEdgeId: $graph.selectedEdgeId,
                                       reduceMotion: reduceMotion, active: scenePhase == .active,
                                       highContrast: contrast == .increased,
                                       colorScheme: scheme, zoom: zoom, zoomRevision: zoomRevision)
                            .accessibilityHidden(true)
                            .overlay(alignment: .topLeading) {
                                Text("\(graph.nodes.count) nodes  ·  \(graph.edges.count) relationships")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .padding(Spacing.s)
                                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: Radius.s))
                                    .padding(Spacing.m)
                                    .accessibilityHidden(true)
                            }
                    }
                    if graph.loading {
                        ProgressView("Loading neighborhood")
                            .padding(Spacing.m)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: Radius.m))
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .overlay(alignment: .bottomTrailing) {
                    if graph.selectedNodeId != nil || graph.selectedEdgeId != nil {
                        inspection
                            .frame(width: 285)
                            .padding(Spacing.m)
                    }
                }
            }
            if let error = graph.error {
                Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill")
                    .padding(Spacing.s)
            } else if let message = graph.message {
                Text(message).font(.caption).foregroundStyle(.secondary).padding(Spacing.s)
            }
        }
        .confirmationDialog("Reject this source-backed relationship?", isPresented: $confirmRejection) {
            Button("Reject relationship", role: .destructive) { graph.reviewSelectedRelation("rejected") }
            Button("Cancel", role: .cancel) {}
        }
        .onAppear { graph.activate() }
        .onDisappear { graph.deactivate() }
        .onChange(of: graph.selectedNodeId) { _ in graph.inspectSelectedNode() }
        .onChange(of: graph.selectedEdgeId) { _ in graph.inspectSelectedRelation() }
        .onChange(of: graph.entityType) { _ in graph.refresh() }
        .onChange(of: graph.predicate) { _ in graph.refresh() }
        .onChange(of: graph.evidenceStatus) { _ in graph.refresh() }
        .onChange(of: graph.collectionId) { _ in graph.refresh() }
        .onChange(of: graph.sourceId) { _ in graph.refresh() }
        .onChange(of: graph.includeSuggestions) { allowed in
            if !allowed && graph.evidenceStatus == "suggested" {
                graph.evidenceStatus = ""
            } else {
                graph.refresh()
            }
        }
    }

    private var toolbar: some View {
        HStack(spacing: Spacing.s) {
            TextField("Search knowledge", text: $graph.search)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 260)
                .onSubmit { graph.refresh() }
                .accessibilityLabel("Find a knowledge entity")
            Button { graph.refresh() } label: { Label("Search", systemImage: "magnifyingglass") }
                .disabled(graph.loading)
            Spacer()
            Toggle(isOn: $graph.includeSuggestions) {
                Label("Suggestions", systemImage: "questionmark.circle")
            }
            .toggleStyle(.button)
            .help("Show unconfirmed model suggestions separately from confirmed knowledge")
            Button { zoom = min(2.5, zoom * 1.2); zoomRevision += 1 } label: { Image(systemName: "minus.magnifyingglass") }
                .accessibilityLabel("Zoom out")
            Button { zoom = max(0.35, zoom / 1.2); zoomRevision += 1 } label: { Image(systemName: "plus.magnifyingglass") }
                .accessibilityLabel("Zoom in")
            Button { zoom = 1; zoomRevision += 1 } label: { Image(systemName: "arrow.up.left.and.down.right.magnifyingglass") }
                .accessibilityLabel("Reset zoom")
            Button { graph.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
        }
        .buttonStyle(.borderless)
        .padding(Spacing.m)
    }

    private var discovery: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack {
                Text("Explore").font(.headline)
                Spacer()
                if graph.loading { ProgressView().controlSize(.small) }
            }
            Picker("Collection", selection: $graph.collectionId) {
                Text("All collections").tag("")
                ForEach(collections) { collection in Text(collection.name).tag(collection.collectionId) }
            }
            Picker("Source", selection: $graph.sourceId) {
                Text("Any source").tag("")
                ForEach(sources.filter { $0.sourceStatus == "active" }) { source in
                    Text(source.title).tag(source.sourceId)
                }
            }
            Picker("Entity type", selection: $graph.entityType) {
                Text("All types").tag("")
                ForEach(KnowledgeVocabulary.entityTypes, id: \.self) { type in
                    Text(KnowledgeVocabulary.label(type).capitalized).tag(type)
                }
            }
            Picker("Relationship", selection: $graph.predicate) {
                Text("Any relationship").tag("")
                ForEach(KnowledgeVocabulary.predicates, id: \.self) { predicate in
                    Text(KnowledgeVocabulary.label(predicate).capitalized).tag(predicate)
                }
            }
            Picker("Evidence", selection: $graph.evidenceStatus) {
                Text("Any confirmed evidence").tag("")
                ForEach(["owner", "accepted", "verified", "documented"], id: \.self) { status in
                    Text(status.capitalized).tag(status)
                }
                if graph.includeSuggestions {
                    Text("Suggested").tag("suggested")
                    Text("Contradicted by verifier").tag("contradicted")
                }
            }
            if !graph.seeds.isEmpty {
                Divider()
                Text("Results").font(.subheadline.weight(.semibold))
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: Spacing.xs) {
                        ForEach(graph.seeds) { entity in
                            Button { graph.focus(entity.entityId) } label: {
                                HStack {
                                    Image(systemName: EntitySymbol.of(entity.entityType)).frame(width: 18)
                                    Text(entity.label).lineLimit(1)
                                    Spacer()
                                    if entity.status == "suggested" {
                                        Image(systemName: "questionmark.circle").foregroundStyle(.orange)
                                    }
                                }
                                .font(.callout)
                                .padding(.vertical, Spacing.xs)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel("Focus \(entity.label), \(entity.status)")
                        }
                    }
                }
                .frame(maxHeight: 150)
            }
            Divider()
            Text("Visible nodes and relationships").font(.subheadline.weight(.semibold))
            List {
                ForEach(graph.nodes) { node in
                    Button { graph.selectNode(node.entityId) } label: {
                        Label(node.label, systemImage: EntitySymbol.of(node.entityType))
                    }
                    .accessibilityLabel("\(node.label), \(node.status), \(node.mentions) mentions")
                }
                ForEach(graph.edges) { edge in
                    Button { graph.selectEdge(edge.relationId) } label: {
                        Label(relationTitle(edge), systemImage: "arrow.right")
                    }
                    .accessibilityLabel("\(relationTitle(edge)), \(edge.evidenceStatus)")
                }
            }
            .listStyle(.plain)
            if graph.canLoadMore {
                Button("Load more connections") { graph.loadMore() }
                    .disabled(graph.loading)
            }
        }
        .padding(Spacing.m)
        .frame(maxHeight: .infinity, alignment: .top)
    }

    private var inspection: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            if graph.inspectingNode {
                ProgressView("Checking current entity")
            } else if let node = graph.inspectedNode {
                HStack {
                    Image(systemName: EntitySymbol.of(node.entityType))
                    Text(node.label).font(.headline).lineLimit(2)
                }
                Text("\(KnowledgeVocabulary.label(node.entityType)) · \(node.status) · \(Count.label(node.sources, "source"))")
                    .font(.caption).foregroundStyle(.secondary)
                if node.typeConflict {
                    Text("Conflicting evidence types").font(.caption).foregroundStyle(.orange)
                }
                if let evidence = node.evidence { evidenceSummary(evidence) }
                HStack {
                    Button("Details") { inspect(node.entityId) }
                    Button("Focus") { graph.focus(node.entityId) }
                }
                Button("Expand neighbors") { graph.expandSelected() }
                    .disabled(node.entityId == graph.focusId || graph.loading ||
                              graph.nodes.count >= GraphBudget.maximumNodes)
            } else if graph.inspectingRelation {
                ProgressView("Checking current relationship")
            } else if let edge = graph.inspectedEdge {
                Text(relationTitle(edge)).font(.headline)
                Text("Direction: subject → object · Origin: \(edge.origin)")
                    .font(.caption).foregroundStyle(.secondary)
                StatusPill(text: edge.evidenceStatus.capitalized,
                           tone: ["suggested", "contradicted"].contains(edge.evidenceStatus) ? .warning : .neutral)
                let alternatives = graph.edges.filter { other in
                    other.relationId != edge.relationId &&
                        Set([other.subjectId, other.objectId]) == Set([edge.subjectId, edge.objectId])
                }
                if !alternatives.isEmpty {
                    Text("Other visible observations").font(.subheadline.weight(.semibold))
                    ForEach(alternatives) { other in
                        Button("\(KnowledgeVocabulary.label(other.predicate)) · \(other.evidenceStatus)") {
                            graph.selectEdge(other.relationId)
                        }
                        .buttonStyle(.link)
                    }
                    Text("Separate observations do not necessarily contradict each other.")
                        .font(.caption2).foregroundStyle(.secondary)
                }

                if let evidence = edge.evidence {
                    evidenceSummary(evidence)
                    HStack {
                        Button("Open current source") { graph.openSelectedEvidence(openSource) }
                            .help("Opens the current Library source when available; otherwise shows its verified text excerpt.")
                            .disabled(graph.reviewingRelation)
                        if evidence.status == "pending" {
                            Button("Accept") { graph.reviewSelectedRelation("accepted") }
                                .disabled(graph.reviewingRelation)
                        }
                        Button("Reject…", role: .destructive) { confirmRejection = true }
                            .disabled(graph.reviewingRelation)
                    }
                } else {
                    Text("Owner assertion. No source citation is attached; this is not a documented source claim.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Button("Ask Praxis…") { graph.prepareExplanation(explain) }
                    .help("Prepare a draft using revalidated relation identifiers. Review the model and text before sending.")
                if let excerpt = graph.sourceExcerpt {
                    Text(excerpt).font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                }
            }
            if let message = graph.inspectionMessage {
                Text(message).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(Spacing.l)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: Radius.l))
    }

    private func evidenceSummary(_ evidence: GraphEvidence) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            Text(evidence.text).font(.callout).lineLimit(4).textSelection(.enabled)
            Text("Source: \(evidence.locator) · revision \(evidence.revision) · \(evidence.status)")
                .font(.caption2).foregroundStyle(.secondary)
            Text("Evidence offset: \(evidence.offset) · verifier: \(evidence.verdict ?? "not recorded")")
                .font(.caption2).foregroundStyle(.secondary)
            Text("The source path is not a PDF page, row, or media timestamp.")
                .font(.caption2).foregroundStyle(.tertiary)
        }
    }

    private func relationTitle(_ edge: GraphEdge) -> String {
        let subject = graph.nodes.first { $0.entityId == edge.subjectId }?.label ?? "Entity"
        let object = graph.nodes.first { $0.entityId == edge.objectId }?.label ?? "Entity"
        return "\(subject) \(KnowledgeVocabulary.label(edge.predicate)) \(object)"
    }
}
