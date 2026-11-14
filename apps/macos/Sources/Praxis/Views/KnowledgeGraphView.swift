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
                    .frame(width: 225)
                Divider()
                ZStack {
                    if graph.focusId == nil, let atlasError = graph.atlasError {
                        EmptyState(systemImage: "exclamationmark.triangle",
                                   title: "Current brain graph unavailable", message: atlasError)
                    } else if graph.visibleNodes.isEmpty && !graph.atlasLoading && !graph.loading {
                        EmptyState(systemImage: "point.3.connected.trianglepath.dotted",
                                   title: "No current graph evidence",
                                   message: graph.atlasError ?? "No source-backed relationships or visible entities are available in this scope.")
                    } else {
                        GraphSceneView(nodes: graph.visibleNodes, edges: graph.visibleEdges, focusId: graph.focusId,
                                       highlightedNodeIds: graph.highlightedNodeIds,
                                       recenterNodeId: graph.recenterNodeId, recenterRevision: graph.recenterRevision,
                                       selectedNodeId: $graph.selectedNodeId, selectedEdgeId: $graph.selectedEdgeId,
                                       reduceMotion: reduceMotion, active: scenePhase == .active,
                                       highContrast: contrast == .increased,
                                       colorScheme: scheme, zoom: zoom, zoomRevision: zoomRevision)
                            .accessibilityHidden(true)
                            .overlay(alignment: .topLeading) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(graph.focusId == nil ? "ONE BRAIN · GLOBAL ATLAS" : "FOCUSED CONNECTIONS")
                                        .font(.caption2.weight(.semibold))
                                    Text("\(graph.visibleNodes.count) visible nodes · \(graph.visibleEdges.count) real connections")
                                        .font(.caption).foregroundStyle(.secondary)
                                    if graph.focusId == nil {
                                        Text("\(graph.overview?.entityCount ?? 0) current entities in scope · rendered relationship sample")
                                            .font(.caption2).foregroundStyle(.tertiary)
                                    }
                                }
                                .padding(Spacing.s)
                                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: Radius.s))
                                .padding(Spacing.m)
                                .accessibilityHidden(true)
                            }
                    }
                    if graph.loading || graph.atlasLoading {
                        ProgressView(graph.focusId == nil ? "Loading current atlas" : "Loading neighborhood")
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
            if !allowed && ["suggested", "contradicted"].contains(graph.evidenceStatus) {
                graph.evidenceStatus = ""
            } else {
                graph.refresh()
            }
        }
    }

    private var toolbar: some View {
        HStack(spacing: Spacing.s) {
            if graph.focusId != nil {
                Button { graph.showOverview() } label: {
                    Label("All knowledge", systemImage: "arrow.left")
                }
                .help("Return to the full scoped knowledge overview")
            }
            TextField("Search knowledge", text: $graph.search)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 260)
                .onSubmit { graph.searchNames() }
                .accessibilityLabel("Find a knowledge entity")
            Button { graph.searchNames() } label: { Label("Find", systemImage: "magnifyingglass") }
                .disabled(graph.loading)
            Spacer()
            Toggle(isOn: $graph.includeSuggestions) {
                Label("Suggestions", systemImage: "questionmark.circle")
            }
            .toggleStyle(.button)
            .help("Show unconfirmed model suggestions separately from confirmed knowledge")
            Button { zoom = min(2.5, zoom * 1.2); zoomRevision += 1 } label: { Image(systemName: "minus.magnifyingglass") }
                .accessibilityLabel("Zoom out")
            Button { zoom = max(0.12, zoom / 1.2); zoomRevision += 1 } label: { Image(systemName: "plus.magnifyingglass") }
                .accessibilityLabel("Zoom in")
            Button { zoom = 1; zoomRevision += 1 } label: { Image(systemName: "viewfinder") }
                .accessibilityLabel("Fit knowledge atlas")
            Button { graph.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
        }
        .buttonStyle(.borderless)
        .padding(Spacing.m)
    }

    private var discovery: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack {
                Text("Contexts").font(.headline)
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
            if let overview = graph.overview {
                Text("\(overview.entityCount.formatted()) current entities · \(graph.visibleNodes.count) on canvas")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if graph.atlasTruncated {
                Text("Global view is bounded; more graph evidence exists.")
                    .font(.caption2).foregroundStyle(.secondary)
            }
            if let atlasError = graph.atlasError {
                Text(atlasError).font(.caption2).foregroundStyle(.orange)
            }
            if !graph.seeds.isEmpty {
                Divider()
                Text("Explore entities").font(.subheadline.weight(.semibold))
                Text("\(graph.seeds.count) loaded · up to \(GraphBudget.maximumNodes) browse results. Search for other names.")
                    .font(.caption2).foregroundStyle(.secondary)
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: Spacing.xs) {
                        ForEach(graph.seeds) { entity in
                            Button { graph.highlightNode(entity.entityId) } label: {
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
                            .accessibilityLabel("Locate \(entity.label), \(entity.status)")
                        }
                    }
                }
                .frame(maxHeight: graph.focusId == nil ? .infinity : 200)
                if graph.nextSeedCursor != nil {
                    Button("More results") { graph.loadMoreSeeds() }
                        .disabled(graph.loading)
                }
            }
            if graph.focusId != nil {
                Divider()
                Text("Focused neighborhood").font(.subheadline.weight(.semibold))
                List {
                    ForEach(graph.nodes) { node in
                        Button { graph.selectNode(node.entityId) } label: {
                            Label(node.label, systemImage: EntitySymbol.of(node.entityType))
                        }
                        .accessibilityLabel("\(node.label), \(node.status), \(node.mentions) mentions")
                    }
                    ForEach(GraphRelationGroup.group(graph.edges)) { group in
                        if let first = group.observations.first {
                            if group.observations.count == 1 {
                                Button { graph.selectEdge(first.relationId) } label: {
                                    Label(relationTitle(first), systemImage: "arrow.right")
                                }
                                .accessibilityLabel("\(relationTitle(first)), \(first.evidenceStatus)")
                            } else {
                                DisclosureGroup("\(relationTitle(first)) · \(group.observations.count) observations") {
                                    ForEach(group.observations) { edge in
                                        Button { graph.selectEdge(edge.relationId) } label: {
                                            Label(edge.evidence.map { "\(edge.evidenceStatus.capitalized) · revision \($0.revision)" }
                                                      ?? "Owner assertion", systemImage: "doc.text.magnifyingglass")
                                        }
                                        .accessibilityLabel("\(relationTitle(edge)), \(edge.evidenceStatus)")
                                    }
                                }
                            }
                        }
                    }
                }
                .listStyle(.plain)
                if graph.canLoadMore {
                    Button("Load more connections") { graph.loadMore() }
                        .disabled(graph.loading)
                }
            }
        }
        .padding(Spacing.m)
        .frame(maxHeight: .infinity, alignment: .top)
    }

    private var inspection: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            if graph.inspectingNode {
                ProgressView("Checking current entity")
            } else if let node = graph.inspectedNode, node.entityId == graph.selectedNodeId {
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
                if graph.focusId != nil {
                    Button("Expand neighbors") { graph.expandSelected() }
                        .disabled(node.entityId == graph.focusId || graph.loading ||
                                  graph.nodes.count >= GraphBudget.maximumNodes)
                }
            } else if graph.inspectingRelation {
                ProgressView("Checking current relationship")
            } else if let edge = graph.inspectedEdge, edge.relationId == graph.selectedEdgeId {
                Text(relationTitle(edge)).font(.headline)
                Text("Direction: subject → object · Origin: \(edge.origin)")
                    .font(.caption).foregroundStyle(.secondary)
                StatusPill(text: edge.evidenceStatus.capitalized,
                           tone: ["suggested", "contradicted"].contains(edge.evidenceStatus) ? .warning : .neutral)
                let alternatives = graph.visibleEdges.filter { other in
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
                HStack {
                    Button("Inspect subject") { inspect(edge.subjectId) }
                    Button("Inspect object") { inspect(edge.objectId) }
                }
                .help("Open canonical entity details for owner-managed names, types and relationships.")
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
        let subject = graph.visibleNodes.first { $0.entityId == edge.subjectId }?.label ?? "Entity"
        let object = graph.visibleNodes.first { $0.entityId == edge.objectId }?.label ?? "Entity"
        return "\(subject) \(KnowledgeVocabulary.label(edge.predicate)) \(object)"
    }
}
