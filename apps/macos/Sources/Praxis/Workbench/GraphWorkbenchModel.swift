import Combine
import Foundation

/// Holds only the visible, permitted projection. Layout and selections are presentation state.
@MainActor
final class GraphWorkbenchModel: ObservableObject {
    private let channel: WorkerChannel
    private let seedScope = "graph-seeds"
    private let neighborhoodScope = "graph-neighborhood"
    private let inspectionScope = "graph-inspection"
    private let previewScope = "graph-preview"
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
    @Published private(set) var inspectedEdge: GraphEdge?
    @Published private(set) var inspectingRelation = false
    @Published private(set) var sourceExcerpt: String?
    @Published private(set) var inspectionMessage: String?
    @Published private(set) var reviewingRelation = false
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
        clearInspection()
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
        clearInspection()
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
        clearInspection()
        focusId = id
        selectedNodeId = id
        selectedEdgeId = nil
        clearInspection()
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
        inspectSelectedRelation()
    }

    func inspectSelectedRelation() {
        guard let id = selectedEdgeId, let edge = edges.first(where: { $0.relationId == id }) else {
            clearInspection()
            return
        }
        validate(edge, then: { [weak self] current in self?.inspectedEdge = current })
    }

    func openSelectedEvidence() {
        guard let edge = inspectedEdge, let evidence = edge.evidence else { return }
        validate(edge) { [weak self] current in
            guard let self, let evidence = current.evidence else { return }
            channel.reset(previewScope)
            sourceExcerpt = nil
            inspectionMessage = nil
            channel.send("preview", [
                "itemId": evidence.itemId, "revision": evidence.revision,
                "sourceId": evidence.sourceId, "originalSha256": evidence.sourceDigest
            ], as: GraphPreview.self, scope: previewScope,
               failed: { [weak self] message in self?.inspectionMessage = message }) { [weak self] result in
                guard let self, active, selectedEdgeId == current.relationId else { return }
                guard let content = result.content, let range = content.range(of: evidence.text) else {
                    inspectionMessage = "No exact current text anchor is available for this evidence."
                    return
                }
                let offset = content.distance(from: content.startIndex, to: range.lowerBound)
                let start = content.index(content.startIndex, offsetBy: max(0, offset - 200))
                let end = content.index(range.upperBound, offsetBy: min(400, content.distance(from: range.upperBound, to: content.endIndex)))
                sourceExcerpt = String(content[start..<end])
            }
        }
    }

    func reviewSelectedRelation(_ decision: String) {
        guard decision == "accepted" || decision == "rejected",
              let edge = inspectedEdge, edge.evidence != nil, !reviewingRelation else { return }
        validate(edge) { [weak self] current in
            guard let self, let evidence = current.evidence else { return }
            guard decision == "rejected" || evidence.status == "pending" else {
                inspectionMessage = "This relationship is no longer awaiting approval."
                return
            }
            reviewingRelation = true
            channel.send("topicReview", ["proposalId": evidence.proposalId, "decision": decision],
                         as: GraphReviewOutcome.self,
                         failed: { [weak self] error in
                             self?.reviewingRelation = false
                             self?.inspectionMessage = error
                         }) { [weak self] result in
                guard let self else { return }
                reviewingRelation = false
                if result.reviewed {
                    refresh()
                    message = "Relationship review saved. The graph was refreshed."
                } else {
                    refresh()
                    message = "The relationship changed or is no longer eligible for review."
                }
            }
        }
    }

    private func validate(_ edge: GraphEdge, then apply: @escaping (GraphEdge) -> Void) {
        guard active, selectedEdgeId == edge.relationId else { return }
        channel.reset(inspectionScope)
        channel.reset(previewScope)
        inspectedEdge = nil
        sourceExcerpt = nil
        inspectionMessage = nil
        inspectingRelation = true
        var fields = scope()
        fields["entityId"] = edge.subjectId
        fields["relationId"] = edge.relationId
        if !predicate.isEmpty { fields["predicate"] = predicate }
        if !evidenceStatus.isEmpty { fields["evidenceStatus"] = evidenceStatus }
        channel.send("graphRelation", fields, as: GraphPage.self, scope: inspectionScope,
                     failed: { [weak self] error in
                         self?.inspectingRelation = false
                         self?.inspectionMessage = error
                     }) { [weak self] page in
            guard let self, active, selectedEdgeId == edge.relationId else { return }
            inspectingRelation = false
            guard page.outcome == "ready", let current = page.edges.first,
                  current.relationId == edge.relationId else {
                refresh()
                message = "That connection is no longer visible in the selected sources."
                return
            }
            apply(current)
        }
    }

    private func clearInspection() {
        channel.reset(inspectionScope)
        channel.reset(previewScope)
        inspectedEdge = nil
        sourceExcerpt = nil
        inspectionMessage = nil
        inspectingRelation = false
        reviewingRelation = false
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
