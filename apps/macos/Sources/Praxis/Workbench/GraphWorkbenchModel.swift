import Combine
import Foundation

/// Holds only the visible, permitted projection. Layout and selections are presentation state.
@MainActor
final class GraphWorkbenchModel: ObservableObject {
    private let channel: WorkerChannel
    private let seedScope = "graph-seeds"
    private let neighborhoodScope = "graph-neighborhood"
    private var active = false
    private var currentExpansion: String?

    @Published var search = ""
    @Published var entityType = ""
    @Published var predicate = ""
    @Published var evidenceStatus = ""
    @Published var collectionId = ""
    @Published var sourceId = ""
    @Published var includeSuggestions = false
    @Published private(set) var seeds: [GraphNode] = []
    @Published private(set) var nodes: [GraphNode] = []
    @Published private(set) var edges: [GraphEdge] = []
    @Published private(set) var focusId: String?
    @Published var selectedNodeId: String?
    @Published var selectedEdgeId: String?
    @Published private(set) var nextCursor: String?
    @Published private(set) var loading = false
    @Published private(set) var outcome = "empty"
    @Published private(set) var error: String?
    @Published private(set) var message: String?

    var selectedNode: GraphNode? { nodes.first { $0.entityId == selectedNodeId } }
    var selectedEdge: GraphEdge? { edges.first { $0.relationId == selectedEdgeId } }
    var canLoadMore: Bool {
        nextCursor != nil && nodes.count < GraphBudget.maximumNodes && edges.count < GraphBudget.maximumEdges
    }

    init(channel: WorkerChannel) { self.channel = channel }

    func activate() {
        guard !active else { return }
        active = true
        refresh()
    }

    func deactivate() {
        guard active else { return }
        active = false
        channel.reset(seedScope)
        channel.reset(neighborhoodScope)
        seeds = []
        nodes = []
        edges = []
        nextCursor = nil
        focusId = nil
        selectedNodeId = nil
        selectedEdgeId = nil
        loading = false
        error = nil
        message = nil
    }

    func refresh() {
        guard active else { return }
        channel.reset(seedScope)
        channel.reset(neighborhoodScope)
        seeds = []
        nodes = []
        edges = []
        selectedNodeId = nil
        selectedEdgeId = nil
        focusId = nil
        nextCursor = nil
        outcome = "empty"
        error = nil
        message = nil
        loading = true

        var fields = scope()
        let text = search.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.isEmpty { fields["search"] = String(text.prefix(120)) }
        fields["limit"] = GraphBudget.seedCount
        channel.send("graphSeeds", fields, as: GraphPage.self, scope: seedScope,
                     failed: { [weak self] error in self?.fail(error) }) { [weak self] page in
            guard let self, active else { return }
            loading = false
            seeds = page.nodes
            outcome = page.outcome
            if let first = seeds.first { focus(first.entityId) }
        }
    }

    func focus(_ id: String) {
        guard active, (seeds + nodes).contains(where: { $0.entityId == id }) else { return }
        channel.reset(neighborhoodScope)
        focusId = id
        selectedNodeId = id
        selectedEdgeId = nil
        nodes = []
        edges = []
        nextCursor = nil
        currentExpansion = nil
        error = nil
        message = nil
        loadNeighborhood(id: id, cursor: nil, replace: true)
    }

    func selectNode(_ id: String?) {
        guard let id, nodes.contains(where: { $0.entityId == id }) else {
            selectedNodeId = nil
            selectedEdgeId = nil
            return
        }
        selectedNodeId = id
        selectedEdgeId = nil
    }

    func selectEdge(_ id: String?) {
        guard let id, edges.contains(where: { $0.relationId == id }) else { return }
        selectedEdgeId = id
        selectedNodeId = nil
    }

    func expandSelected() {
        guard let selectedNodeId, !loading, active, selectedNodeId != focusId,
              nodes.count < GraphBudget.maximumNodes, edges.count < GraphBudget.maximumEdges else { return }
        channel.reset(neighborhoodScope)
        nextCursor = nil
        loadNeighborhood(id: selectedNodeId, cursor: nil, replace: false)
    }

    func loadMore() {
        guard canLoadMore, let focusId, let nextCursor, !loading else { return }
        loadNeighborhood(id: focusId, cursor: nextCursor, replace: false)
    }

    private func loadNeighborhood(id: String, cursor: String?, replace: Bool) {
        guard active else { return }
        loading = true
        error = nil
        currentExpansion = id
        var fields = scope()
        fields["entityId"] = id
        fields["limit"] = min(GraphBudget.neighborhoodPageSize,
                              GraphBudget.maximumEdges - (replace ? 0 : edges.count))
        if let cursor { fields["cursor"] = cursor }
        if !predicate.isEmpty { fields["predicate"] = predicate }
        if !evidenceStatus.isEmpty { fields["evidenceStatus"] = evidenceStatus }
        channel.send("graphNeighborhood", fields, as: GraphPage.self, scope: neighborhoodScope,
                     failed: { [weak self] error in self?.fail(error) }) { [weak self] page in
            guard let self, active, currentExpansion == id else { return }
            loading = false
            outcome = page.outcome
            if page.outcome == "unavailable" {
                nodes = []
                edges = []
                seeds.removeAll { $0.entityId == id }
                focusId = nil
                selectedNodeId = nil
                selectedEdgeId = nil
                nextCursor = nil
                message = "That entity is no longer available in the selected sources."
                return
            }
            if replace {
                nodes = []
                edges = []
            }
            let merged = GraphProjection.merge(nodes: nodes, edges: edges, page: page)
            nodes = merged.nodes
            edges = merged.edges
            if replace || id == focusId {
                nextCursor = page.nextCursor
            } else {
                nextCursor = nil
            }
            if nodes.count >= GraphBudget.maximumNodes || edges.count >= GraphBudget.maximumEdges {
                nextCursor = nil
                message = "Showing a bounded neighborhood. Focus another entity to explore further."
            }
        }
    }

    private func scope() -> [String: Any] {
        var fields: [String: Any] = [:]
        if !entityType.isEmpty { fields["entityType"] = entityType }
        if !collectionId.isEmpty { fields["collectionId"] = collectionId }
        if !sourceId.isEmpty { fields["sourceId"] = sourceId }
        if includeSuggestions { fields["includeSuggestions"] = true }
        return fields
    }

    private func fail(_ description: String) {
        guard active else { return }
        loading = false
        nextCursor = nil
        error = description
    }
}
