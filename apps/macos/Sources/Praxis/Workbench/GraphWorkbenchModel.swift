import Combine
import Foundation

/// Holds only the visible, permitted projection. Layout and selections are presentation state.
@MainActor
final class GraphWorkbenchModel: ObservableObject {
    private let channel: WorkerChannel
    private let seedScope = "graph-seeds"
    private let overviewScope = "graph-overview"
    private let conceptScope = "graph-concepts"
    private let childConceptScope = "graph-child-concepts"
    private var conceptTrail: [String] = []
    private let atlasScope = "graph-atlas"
    private let neighborhoodScope = "graph-neighborhood"
    private let inspectionScope = "graph-inspection"
    private let previewScope = "graph-preview"
    private let watchScope = "graph-watch"
    private let nodeScope = "graph-node-inspection"
    private let nodeWatchScope = "graph-node-watch"
    private var active = false
    private var currentExpansion: String?
    private var requestedFocusId: String?
    private var pendingLocateNodeId: String?
    private var atlasBufferNodes: [GraphNode] = []
    private var atlasBufferEdges: [GraphEdge] = []

    @Published var search = ""
    @Published var entityType = ""
    @Published var predicate = ""
    @Published var evidenceStatus = ""
    @Published var collectionId = ""
    @Published var sourceId = ""
    @Published var includeSuggestions = false
    @Published private(set) var seeds: [GraphNode] = []
    @Published private(set) var overview: GraphOverview?
    @Published private(set) var zoneOverview: GraphZoneOverview?
    @Published private(set) var zoneLoading = false
    @Published private(set) var conceptRegions: [GraphConceptRegion] = []
    @Published private(set) var conceptRegionCount = 0
    @Published private(set) var conceptNextCursor: String?
    @Published private(set) var conceptLoading = false
    @Published private(set) var conceptError: String?
    @Published private(set) var childConcepts: GraphConceptPage?
    @Published private(set) var childConceptLoading = false
    @Published private(set) var childConceptError: String?

    var isBrowsingConcept: Bool { !conceptTrail.isEmpty }

    @Published private(set) var selectedZoneType: String?
    @Published private(set) var atlasNodes: [GraphNode] = []
    @Published private(set) var atlasEdges: [GraphEdge] = []
    @Published private(set) var atlasLoading = false
    @Published private(set) var atlasTruncated = false
    @Published private(set) var atlasError: String?
    @Published private(set) var recenterNodeId: String?
    @Published private(set) var recenterRevision = 0
    @Published private(set) var nextSeedCursor: String?
    @Published private(set) var nodes: [GraphNode] = []
    @Published private(set) var edges: [GraphEdge] = []
    @Published private(set) var focusId: String?
    @Published var selectedNodeId: String?
    @Published var selectedEdgeId: String?
    @Published private(set) var nextCursor: String?
    @Published private(set) var inspectedEdge: GraphEdge?
    @Published private(set) var inspectedNode: GraphNode?
    @Published private(set) var inspectingNode = false
    @Published private(set) var inspectingRelation = false
    @Published private(set) var sourceExcerpt: String?
    @Published private(set) var inspectionMessage: String?
    @Published private(set) var reviewingRelation = false
    @Published private(set) var loading = false
    @Published private(set) var outcome = "empty"
    @Published private(set) var error: String?
    @Published private(set) var message: String?
    /// Knowledge changed since the graph was loaded (intake, reviews, revisions). The view offers a
    /// refresh instead of resetting the camera under the owner.
    @Published private(set) var knowledgeChanged = false
    private var loadedRevision: Int64?

    var visibleNodes: [GraphNode] {
        guard focusId == nil else { return nodes }
        guard selectedZoneType != nil, !atlasLoading else { return [] }
        let ids = Set(atlasNodes.map(\.entityId))
        return atlasNodes + Array(seeds.filter { !ids.contains($0.entityId) }
            .prefix(GraphBudget.seedCount))
    }

    var visibleEdges: [GraphEdge] {
        if focusId != nil { return edges }
        return selectedZoneType == nil ? [] : atlasEdges
    }

    var regionalRelationshipCount: Int? {
        guard let selectedZoneType, let zoneOverview else { return nil }
        return zoneOverview.links
            .filter { $0.subjectType == selectedZoneType || $0.objectType == selectedZoneType }
            .reduce(0) { $0 + $1.count }
    }

    var highlightedNodeIds: Set<String> {
        guard !search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return [] }
        return Set(seeds.map(\.entityId))
    }

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
        channel.reset(overviewScope)
        channel.reset(conceptScope)
        resetConceptTrail()
        channel.reset(atlasScope)
        channel.reset(neighborhoodScope)
        clearInspection()
        overview = nil
        zoneOverview = nil
        zoneLoading = false
        conceptRegions = []
        conceptRegionCount = 0
        conceptNextCursor = nil
        conceptLoading = false
        conceptError = nil
        selectedZoneType = nil
        pendingLocateNodeId = nil
        atlasNodes = []
        atlasEdges = []
        atlasBufferNodes = []
        atlasBufferEdges = []
        atlasLoading = false
        atlasTruncated = false
        atlasError = nil
        nextSeedCursor = nil
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
        channel.reset(overviewScope)
        channel.reset(conceptScope)
        resetConceptTrail()
        channel.reset(atlasScope)
        channel.reset(neighborhoodScope)
        clearInspection()
        overview = nil
        zoneOverview = nil
        zoneLoading = false
        conceptRegions = []
        conceptRegionCount = 0
        conceptNextCursor = nil
        conceptLoading = false
        conceptError = nil
        selectedZoneType = nil
        pendingLocateNodeId = nil
        atlasNodes = []
        atlasEdges = []
        atlasBufferNodes = []
        atlasBufferEdges = []
        atlasLoading = false
        atlasTruncated = false
        atlasError = nil
        nextSeedCursor = nil
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
        zoneLoading = true

        var fields: [String: Any] = [:]
        if !collectionId.isEmpty { fields["collectionId"] = collectionId }
        if !sourceId.isEmpty { fields["sourceId"] = sourceId }
        if includeSuggestions { fields["includeSuggestions"] = true }
        if !predicate.isEmpty { fields["predicate"] = predicate }
        if !evidenceStatus.isEmpty { fields["evidenceStatus"] = evidenceStatus }
        channel.send("graphZones", fields, as: GraphZoneOverview.self, scope: overviewScope,
                     failed: { [weak self] error in
                         self?.zoneLoading = false
                         self?.error = error
                     }) { [weak self] summary in
            guard let self, active else { return }
            zoneLoading = false
            zoneOverview = summary
            overview = GraphOverview(entityCount: summary.entityCount, types: summary.zones)
        }
        loadSeeds(cursor: nil)
        loadConcepts(cursor: nil)
        knowledgeChanged = false
        loadedRevision = nil
        checkKnowledgeRevision()
    }

    /// Polls the cheap knowledge watermark; the first answer after a load becomes the baseline.
    func checkKnowledgeRevision() {
        guard active else { return }
        channel.send("knowledgeWatermark", [:], as: KnowledgeWatermarkData.self, scope: "graph-watermark",
                     failed: { _ in }) { [weak self] watermark in
            guard let self, active else { return }
            if let loadedRevision {
                knowledgeChanged = watermark.graphRevision > loadedRevision
            } else {
                loadedRevision = watermark.graphRevision
            }
        }
    }

    func loadMoreConcepts() {
        if let parent = conceptTrail.last {
            guard let cursor = childConcepts?.nextCursor, !childConceptLoading else { return }
            loadChildConcepts(parent, cursor: cursor)
            return
        }
        guard conceptNextCursor != nil, !conceptLoading else { return }
        loadConcepts(cursor: conceptNextCursor)
    }

    private func loadConcepts(cursor: String?) {
        guard active else { return }
        conceptLoading = true
        var fields: [String: Any] = ["limit": 32]
        if !collectionId.isEmpty { fields["collectionId"] = collectionId }
        if !sourceId.isEmpty { fields["sourceId"] = sourceId }
        if let cursor { fields["cursor"] = cursor }
        channel.send("graphConceptRegions", fields, as: GraphConceptPage.self, scope: conceptScope,
                     failed: { [weak self] error in
                         self?.conceptLoading = false
                         self?.conceptRegions = []
                         self?.conceptRegionCount = 0
                         self?.conceptNextCursor = nil
                         self?.conceptError = error
                     }) { [weak self] page in
            guard let self, active else { return }
            conceptLoading = false
            conceptError = nil
            conceptRegionCount = page.regionCount
            conceptNextCursor = page.nextCursor
            if cursor == nil {
                conceptRegions = page.regions
            } else {
                let existing = Set(conceptRegions.map(\.regionId))
                conceptRegions += page.regions.filter { !existing.contains($0.regionId) }
            }
        }
    }

    func openConcept(_ id: String) {
        let regions = isBrowsingConcept ? childConcepts?.regions ?? [] : conceptRegions
        guard active, !conceptTrail.contains(id), regions.contains(where: { $0.regionId == id }) else { return }
        conceptTrail.append(id)
        focusValidated(id, preserveConceptTrail: true)
        loadChildConcepts(id, cursor: nil)
    }

    func backFromConcept() {
        guard active, !conceptTrail.isEmpty else { return }
        conceptTrail.removeLast()
        if let parent = conceptTrail.last {
            focusValidated(parent, preserveConceptTrail: true)
            loadChildConcepts(parent, cursor: nil)
        } else {
            showOverview()
        }
    }

    private func resetConceptTrail() {
        conceptTrail = []
        channel.reset(childConceptScope)
        childConcepts = nil
        childConceptLoading = false
        childConceptError = nil
    }

    private func loadChildConcepts(_ parent: String, cursor: String?) {
        channel.reset(childConceptScope)
        if cursor == nil { childConcepts = nil }
        childConceptLoading = true
        childConceptError = nil
        var fields: [String: Any] = ["limit": 32, "parentRegionId": parent]
        if !collectionId.isEmpty { fields["collectionId"] = collectionId }
        if !sourceId.isEmpty { fields["sourceId"] = sourceId }
        if let cursor { fields["cursor"] = cursor }
        channel.send("graphConceptRegions", fields, as: GraphConceptPage.self, scope: childConceptScope,
                     failed: { [weak self] error in
                         self?.childConceptLoading = false
                         self?.childConcepts = nil
                         self?.childConceptError = error
                     }) { [weak self] page in
            guard let self, active, conceptTrail.last == parent else { return }
            childConceptLoading = false
            let previous = cursor == nil ? [] : childConcepts?.regions ?? []
            childConcepts = GraphConceptPage(regionCount: page.regionCount, regions: previous + page.regions,
                                            nextCursor: page.nextCursor, revision: page.revision)
        }
    }

    private func loadAtlas(cursor: String?) {
        guard active, let selectedZoneType else { return }
        atlasLoading = true
        var fields = scope()
        fields["entityType"] = selectedZoneType
        fields["limit"] = GraphBudget.atlasPageSize
        if let cursor { fields["cursor"] = cursor }
        if !predicate.isEmpty { fields["predicate"] = predicate }
        if !evidenceStatus.isEmpty { fields["evidenceStatus"] = evidenceStatus }
        channel.send("graphRegion", fields, as: GraphPage.self, scope: atlasScope,
                     failed: { [weak self] error in
                         self?.atlasLoading = false
                         self?.atlasNodes = []
                         self?.atlasEdges = []
                         self?.atlasBufferNodes = []
                         self?.atlasBufferEdges = []
                         self?.atlasError = "The atlas changed while loading. Refresh to read current knowledge. \(error)"
                     }) { [weak self] page in
            guard let self, active else { return }
            let previousEdges = Set(atlasBufferEdges.map(\.relationId))
            let merged = GraphProjection.merge(nodes: atlasBufferNodes, edges: atlasBufferEdges, page: page,
                                               maximumNodes: GraphBudget.atlasNodes,
                                               maximumEdges: GraphBudget.atlasEdges)
            let admitted = merged.edges.count - previousEdges.count
            let newRelations = page.edges.filter { !previousEdges.contains($0.relationId) }.count
            atlasTruncated = atlasTruncated || admitted < newRelations
            atlasBufferNodes = merged.nodes
            atlasBufferEdges = merged.edges
            let full = atlasBufferNodes.count >= GraphBudget.atlasNodes ||
                atlasBufferEdges.count >= GraphBudget.atlasEdges
            if let next = page.nextCursor, !full {
                loadAtlas(cursor: next)
            } else {
                atlasTruncated = atlasTruncated || page.nextCursor != nil ||
                    (regionalRelationshipCount.map { $0 > merged.edges.count } ?? false)
                atlasNodes = atlasBufferNodes
                atlasEdges = atlasBufferEdges
                atlasLoading = false
                if let pendingLocateNodeId {
                    self.pendingLocateNodeId = nil
                    if visibleNodes.contains(where: { $0.entityId == pendingLocateNodeId }) {
                        highlightNode(pendingLocateNodeId)
                    } else {
                        focusValidated(pendingLocateNodeId)
                    }
                }
            }
        }
    }

    func searchNames() {
        guard active else { return }
        channel.reset(seedScope)
        seeds = []
        nextSeedCursor = nil
        loadSeeds(cursor: nil)
    }

    func highlightNode(_ id: String) {
        guard focusId == nil else {
            focus(id)
            return
        }
        if selectedZoneType == nil, let node = seeds.first(where: { $0.entityId == id }),
           let type = node.entityType, zoneOverview?.zones.contains(where: { $0.entityType == type }) == true {
            pendingLocateNodeId = id
            enterZone(type)
            return
        }
        guard visibleNodes.contains(where: { $0.entityId == id }) else {
            focus(id)
            return
        }
        selectedNodeId = id
        selectedEdgeId = nil
        recenterNodeId = id
        recenterRevision += 1
    }

    func enterZone(_ type: String) {
        guard active, selectedZoneType != type,
              zoneOverview?.zones.contains(where: { $0.entityType == type }) == true else { return }
        channel.reset(atlasScope)
        channel.reset(seedScope)
        clearInspection()
        selectedZoneType = type
        selectedNodeId = nil
        selectedEdgeId = nil
        atlasNodes = []
        atlasEdges = []
        atlasBufferNodes = []
        atlasBufferEdges = []
        atlasTruncated = false
        atlasError = nil
        seeds = []
        nextSeedCursor = nil
        loadSeeds(cursor: nil)
        loadAtlas(cursor: nil)
    }

    func showZones() {
        guard active else { return }
        showOverview()
        channel.reset(atlasScope)
        selectedZoneType = nil
        pendingLocateNodeId = nil
        atlasNodes = []
        atlasEdges = []
        atlasBufferNodes = []
        atlasBufferEdges = []
        atlasLoading = false
        atlasTruncated = false
        atlasError = nil
        channel.reset(seedScope)
        seeds = []
        nextSeedCursor = nil
        loadSeeds(cursor: nil)
    }

    func loadMoreSeeds() {
        guard active, let nextSeedCursor, !loading,
              seeds.count < GraphBudget.maximumNodes else { return }
        loadSeeds(cursor: nextSeedCursor)
    }

    func showOverview() {
        resetConceptTrail()
        if active {
            channel.reset(conceptScope)
            conceptRegions = []
            conceptRegionCount = 0
            conceptNextCursor = nil
            conceptError = nil
            loadConcepts(cursor: nil)
        }
        requestedFocusId = nil
        recenterNodeId = nil
        channel.reset(neighborhoodScope)
        clearInspection()
        focusId = nil
        selectedNodeId = nil
        selectedEdgeId = nil
        nodes = []
        edges = []
        nextCursor = nil
        currentExpansion = nil
        loading = false
        message = nil
    }

    private func loadSeeds(cursor: String?) {
        loading = true
        var fields = scope()
        if let selectedZoneType { fields["entityType"] = selectedZoneType }
        let text = search.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.isEmpty { fields["search"] = String(text.prefix(120)) }
        fields["limit"] = min(GraphBudget.seedCount, GraphBudget.maximumNodes - seeds.count)
        if let cursor { fields["cursor"] = cursor }
        channel.send("graphSeeds", fields, as: GraphPage.self, scope: seedScope,
                     failed: { [weak self] error in self?.fail(error) }) { [weak self] page in
            guard let self, active else { return }
            loading = false
            if cursor == nil {
                seeds = page.nodes
            } else {
                let known = Set(seeds.map(\.entityId))
                seeds += page.nodes.filter { !known.contains($0.entityId) }
            }
            nextSeedCursor = seeds.count < GraphBudget.maximumNodes ? page.nextCursor : nil
            outcome = page.outcome
            if let requestedFocusId {
                self.requestedFocusId = nil
                focusValidated(requestedFocusId)
            }
        }
    }

    func focus(_ id: String) {
        guard (seeds + nodes + atlasNodes).contains(where: { $0.entityId == id }) else { return }
        focusValidated(id)
    }

    func focusEntityFromBrowse(_ id: String) {
        guard UUID(uuidString: id) != nil else { return }
        if active { focusValidated(id) } else { requestedFocusId = id }
    }

    private func focusValidated(_ id: String, preserveConceptTrail: Bool = false) {
        guard active else { return }
        if !preserveConceptTrail { resetConceptTrail() }
        channel.reset(neighborhoodScope)
        clearInspection()
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
        guard let id, visibleNodes.contains(where: { $0.entityId == id }) else {
            selectedNodeId = nil
            selectedEdgeId = nil
            return
        }
        selectedNodeId = id
        selectedEdgeId = nil
    }

    func selectEdge(_ id: String?) {
        guard let id, visibleEdges.contains(where: { $0.relationId == id }) else { return }
        selectedEdgeId = id
        selectedNodeId = nil
    }

    func inspectSelectedRelation() {
        guard let id = selectedEdgeId else {
            if selectedNodeId == nil { clearInspection() }
            return
        }
        guard let edge = visibleEdges.first(where: { $0.relationId == id }) else {
            clearInspection()
            return
        }
        validate(edge)
    }

    func inspectSelectedNode() {
        guard let id = selectedNodeId else {
            if selectedEdgeId == nil { clearInspection() }
            return
        }
        guard active, visibleNodes.contains(where: { $0.entityId == id }) else { return }
        channel.reset(nodeScope)
        channel.reset(nodeWatchScope)
        channel.reset(inspectionScope)
        channel.reset(previewScope)
        channel.reset(watchScope)
        inspectedNode = nil
        inspectedEdge = nil
        sourceExcerpt = nil
        inspectingNode = true
        inspectionMessage = nil
        channel.send("graphNeighborhood", nodeFields(id), as: GraphPage.self, scope: nodeScope,
                     failed: { [weak self] error in
                         self?.inspectingNode = false
                         self?.inspectionMessage = error
                     }) { [weak self] page in
            guard let self, active, selectedNodeId == id else { return }
            inspectingNode = false
            guard page.outcome == "ready", let node = page.nodes.first(where: { $0.entityId == id }) else {
                refresh()
                message = "The selected entity is no longer visible."
                return
            }
            inspectedNode = node
        }
    }

    func recheckInspection() {
        guard active, !inspectingRelation, !inspectingNode, !reviewingRelation else { return }
        if let node = inspectedNode, selectedNodeId == node.entityId {
            recheckNode(node)
            return
        }
        guard let edge = inspectedEdge, selectedEdgeId == edge.relationId else { return }
        let fields = relationFields(edge)
        channel.reset(watchScope)
        channel.send("graphRelation", fields, as: GraphPage.self, scope: watchScope,
                     failed: { [weak self] error in
                         self?.inspectedEdge = nil
                         self?.sourceExcerpt = nil
                         self?.inspectionMessage = "Unable to verify the current relationship: \(error)"
                     }) { [weak self] page in
            guard let self, active, selectedEdgeId == edge.relationId else { return }
            guard page.outcome == "ready", let current = page.edges.first,
                  current.relationId == edge.relationId else {
                refresh()
                message = "The inspected relationship is no longer visible."
                return
            }
            if inspectedEdge != current {
                inspectedEdge = current
                sourceExcerpt = nil
                inspectionMessage = "The relationship evidence changed. Reopen its current source."
            }
        }
    }

    private func recheckNode(_ node: GraphNode) {
        channel.reset(nodeWatchScope)
        channel.send("graphNeighborhood", nodeFields(node.entityId), as: GraphPage.self,
                     scope: nodeWatchScope, failed: { [weak self] error in
                         self?.inspectedNode = nil
                         self?.inspectionMessage = "Unable to verify the current entity: \(error)"
                     }) { [weak self] page in
            guard let self, active, selectedNodeId == node.entityId else { return }
            guard page.outcome == "ready",
                  let current = page.nodes.first(where: { $0.entityId == node.entityId }) else {
                refresh()
                message = "The inspected entity is no longer visible."
                return
            }
            if inspectedNode != current {
                inspectedNode = current
                inspectionMessage = "The entity evidence changed."
            }
        }
    }

    func openSelectedEvidence(_ navigate: @escaping (GraphEvidence) -> Bool) {
        guard let edge = inspectedEdge, edge.evidence != nil else { return }
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
                guard result.outcome != "unavailable" else {
                    inspectionMessage = "The source revision is no longer available."
                    return
                }
                if navigate(evidence) { return }
                guard let content = result.content else {
                    inspectionMessage = "The current source has no readable text."
                    return
                }
                guard let excerpt = evidence.excerpt(in: content) else {
                    inspectionMessage = "No exact current text anchor is available for this evidence."
                    return
                }
                sourceExcerpt = excerpt
            }
        }
    }

    func prepareExplanation(_ deliver: @escaping (String) -> Void) {
        guard let edge = inspectedEdge else { return }
        validate(edge) { [weak self] current in
            guard let self else { return }
            guard ["owner", "accepted", "verified", "documented"].contains(current.evidenceStatus) else {
                inspectionMessage = "Unconfirmed suggestions require owner review before agent investigation."
                return
            }
            let prompt = """
                Explain this currently authorized Praxis relationship using only evidence you can verify now.
                Subject entity ID: \(current.subjectId)
                Predicate: \(current.predicate)
                Object entity ID: \(current.objectId)
                Relation ID: \(current.relationId)
                Origin: \(current.origin); status: \(current.evidenceStatus).
                Do not claim a source citation for an owner-only assertion.
                Recheck current source permissions, revisions and disclosures through the available tools.
                Abstain if evidence is unavailable. Do not create or approve any proposed relation.
                State uncertainties and contradictions.
                """
            deliver(prompt)
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

    private func validate(_ edge: GraphEdge, then apply: ((GraphEdge) -> Void)? = nil) {
        guard active, selectedEdgeId == edge.relationId else { return }
        channel.reset(inspectionScope)
        channel.reset(previewScope)
        channel.reset(watchScope)
        channel.reset(nodeScope)
        channel.reset(nodeWatchScope)
        inspectedNode = nil
        inspectingNode = false
        inspectedEdge = nil
        sourceExcerpt = nil
        inspectionMessage = nil
        inspectingRelation = true
        let fields = relationFields(edge)
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
            inspectedEdge = current
            apply?(current)
        }
    }

    private func clearInspection() {
        channel.reset(inspectionScope)
        channel.reset(previewScope)
        channel.reset(watchScope)
        channel.reset(nodeScope)
        channel.reset(nodeWatchScope)
        inspectedNode = nil
        inspectingNode = false
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
                showOverview()
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
            if replace, let selectedNodeId {
                inspectedNode = nodes.first(where: { $0.entityId == selectedNodeId })
            }
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

    private func nodeFields(_ id: String) -> [String: Any] {
        var fields = scope()
        fields["entityId"] = id
        fields["limit"] = 1
        return fields
    }

    private func relationFields(_ edge: GraphEdge) -> [String: Any] {
        var fields = scope()
        fields["entityId"] = edge.subjectId
        fields["relationId"] = edge.relationId
        if !predicate.isEmpty { fields["predicate"] = predicate }
        if !evidenceStatus.isEmpty { fields["evidenceStatus"] = evidenceStatus }
        return fields
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
