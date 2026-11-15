import CoreGraphics
import Foundation
import SpriteKit
import Testing
@testable import Praxis

@Suite("Native graph projection and layout")
struct GraphPresentationTests {
    private func node(_ id: String, type: String? = "concept", status: String = "confirmed") -> GraphNode {
        GraphNode(entityId: id, label: id, entityType: type, typeOrigin: "inferred",
                  status: status, mentions: 1, sources: 1, typeConflict: false, evidence: nil,
                  actions: ["openEntity"])
    }

    private func edge(_ id: String, _ from: String, _ to: String) -> GraphEdge {
        GraphEdge(relationId: id, subjectId: from, objectId: to, predicate: "references",
                  origin: "owner", evidenceStatus: "owner", evidence: nil, actions: ["inspectRelation"])
    }

    @Test func unifiedOverviewReportsOnlyCanonicalScopeCounts() throws {
        let json = #"{"entityCount":6,"types":[{"entityType":"project","count":4},{"entityType":"technology","count":2}]}"#
        let overview = try JSONDecoder().decode(GraphOverview.self, from: Data(json.utf8))
        #expect(overview.entityCount == 6)
        #expect(overview.types.reduce(0) { $0 + $1.count } == overview.entityCount)
        #expect(overview.types.map(\.entityType) == ["project", "technology"])
    }

    @Test func graphSidebarGroupsAssertionsWithoutCollapsingEvidence() {
        let first = edge("e1", "subject", "object")
        let second = edge("e2", "subject", "object")
        let reverse = edge("e3", "object", "subject")
        let grouped = GraphRelationGroup.group([first, second, reverse])
        #expect(grouped.count == 2)
        #expect(grouped.first(where: { $0.observations.count == 2 })?.observations.map(\.relationId) == ["e1", "e2"])
        #expect(grouped.flatMap(\.observations).count == 3)
    }

    @Test func entityInspectorGroupsEvidenceWithoutMergingTheCanonicalRelations() {
        let source = EntityRelation(relationId: "a", predicate: "uses", outgoing: true,
                                    otherEntityId: "other", otherName: "Dependency",
                                    origin: "model", evidence: "uses Dependency")
        let second = EntityRelation(relationId: "b", predicate: "uses", outgoing: true,
                                    otherEntityId: "other", otherName: "Dependency",
                                    origin: "model", evidence: "separate source")
        let reverse = EntityRelation(relationId: "c", predicate: "uses", outgoing: false,
                                     otherEntityId: "other", otherName: "Dependency",
                                     origin: "owner", evidence: nil)
        let grouped = EntityRelationGroup.group([second, reverse, source])
        #expect(grouped.count == 2)
        #expect(grouped.first(where: { $0.outgoing })?.observations.map(\.relationId) == ["a", "b"])
        #expect(grouped.flatMap(\.observations).count == 3)
    }

    @Test func entityRelationsPlaceStructureBeforeCallsAndPreserveObservations() {
        let observations = [
            EntityRelation(relationId: "call-2", predicate: "calls", outgoing: true,
                           otherEntityId: "callee", otherName: "Render", origin: "model", evidence: "second call"),
            EntityRelation(relationId: "part", predicate: "part_of", outgoing: true,
                           otherEntityId: "class", otherName: "CRadar", origin: "model", evidence: "member"),
            EntityRelation(relationId: "call-1", predicate: "calls", outgoing: true,
                           otherEntityId: "callee", otherName: "Render", origin: "model", evidence: "first call"),
            EntityRelation(relationId: "called", predicate: "calls", outgoing: false,
                           otherEntityId: "caller", otherName: "CRadar_Draw", origin: "owner", evidence: nil),
            EntityRelation(relationId: "inherits", predicate: "inherits_from", outgoing: false,
                           otherEntityId: "derived", otherName: "DerivedRadar", origin: "model", evidence: "inherits")
        ]
        let sections = EntityRelationSection.sections(EntityRelationGroup.group(observations))
        #expect(sections.map(\.title) == ["Inherited by", "Part of", "Calls", "Called by"])
        #expect(sections.flatMap(\.groups).flatMap(\.observations).count == observations.count)
        #expect(sections.first(where: { $0.title == "Calls" })?.groups.first?.observations.map(\.relationId) == ["call-1", "call-2"])
        #expect(EntityRelationSection.collapsedRows == 6)
    }

    @Test func neuralZoneLayoutIsStableAndUsesOnlyScopedAggregateLinks() {
        let zones = [
            GraphTypeCount(entityType: "concept", count: 250),
            GraphTypeCount(entityType: "project", count: 12),
            GraphTypeCount(entityType: "technology", count: 40),
            GraphTypeCount(entityType: "person", count: 2)
        ]
        let links = [
            GraphZoneLink(subjectType: "project", objectType: "concept",
                          confirmedCount: 23, suggestedCount: 0, contradictedCount: 0),
            GraphZoneLink(subjectType: "concept", objectType: "technology",
                          confirmedCount: 5, suggestedCount: 2, contradictedCount: 0)
        ]
        let first = GraphZoneOverview(entityCount: 304, relationshipCount: 30,
                                      zones: zones, links: links)
        let second = GraphZoneOverview(entityCount: 304, relationshipCount: 30,
                                       zones: Array(zones.reversed()), links: Array(links.reversed()))
        let layout = GraphZoneLayout.place(first)
        #expect(layout.positions == GraphZoneLayout.place(second).positions)
        #expect(layout.positions.count == 4)
        #expect(layout.bounds.width > 0 && layout.bounds.height > 0)
        #expect(layout.positions.values.allSatisfy { $0.x.isFinite && $0.y.isFinite })
        #expect(layout.positions["person"] != layout.positions["project"])
        #expect(GraphZoneLayout.place(GraphZoneOverview(entityCount: 0, relationshipCount: 0,
                                                       zones: [], links: [])).positions.isEmpty)
    }

    @MainActor
    @Test func wholeBrainSceneUsesAggregatedZonesWithoutEndpointSprites() {
        let zones = GraphZoneOverview(entityCount: 52, relationshipCount: 7, zones: [
            GraphTypeCount(entityType: "project", count: 2),
            GraphTypeCount(entityType: "concept", count: 50)
        ], links: [
            GraphZoneLink(subjectType: "project", objectType: "concept",
                          confirmedCount: 7, suggestedCount: 0, contradictedCount: 0)
        ])
        let scene = GraphScene()
        scene.size = CGSize(width: 800, height: 600)
        scene.configureZones(zones, type: nil)
        #expect(scene.camera?.xScale == scene.camera?.yScale)
        let endpointSpriteCount = scene.children.filter { $0.name?.hasPrefix("node:") == true }.count
        #expect(endpointSpriteCount == 0)
        scene.reduceMotion = true
        scene.configure(nodes: [node("a"), node("b")], edges: [edge("r", "a", "b")], focus: nil)
        #expect(scene.camera != nil)
    }

    @Test func brainGraphStylesHaveDistinctStablePreferences() {
        #expect(GraphStyle.allCases.count == 2)
        #expect(GraphStyle(rawValue: "neuralNebula") == .neuralNebula)
        #expect(GraphStyle(rawValue: "cellularAtlas") == .cellularAtlas)
        #expect(GraphStyle.neuralNebula.title != GraphStyle.cellularAtlas.title)
    }

    @Test func neuralNebulaPlacementIsThreeDimensionalStableAndScopeBound() {
        let summary = GraphZoneOverview(entityCount: 1003, relationshipCount: 1, zones: [
            GraphTypeCount(entityType: "project", count: 800),
            GraphTypeCount(entityType: "concept", count: 200),
            GraphTypeCount(entityType: "other", count: 3)
        ], links: [
            GraphZoneLink(subjectType: "project", objectType: "concept",
                          confirmedCount: 1, suggestedCount: 0, contradictedCount: 0)
        ])
        let nodes = [node("alpha", type: "project"), node("beta", type: "concept"),
                     node("disconnected", type: "other")]
        let links = [edge("actual", "alpha", "beta")]
        let first = NeuralNebulaLayout.place(zones: summary, nodes: nodes,
                                              edges: links, regionType: "project")
        let again = NeuralNebulaLayout.place(zones: summary, nodes: nodes.reversed(),
                                              edges: links.reversed(), regionType: "project")
        #expect(first.zoneCenters == again.zoneCenters)
        #expect(first.nodeCenters == again.nodeCenters)
        #expect(first.zoneCenters.count == summary.zones.count)
        #expect(first.nodeCenters.count == nodes.count)
        #expect(Set(first.nodeCenters.keys) == Set(nodes.map(\.entityId)))
        #expect(Set(first.zoneCenters.values.map(\.z)).count > 1)
        #expect(first.nodeCenters.values.allSatisfy {
            $0.x.isFinite && $0.y.isFinite && $0.z.isFinite
        })
        #expect(NeuralNebulaLayout.place(zones: summary, nodes: [], edges: [],
                                         regionType: nil).nodeCenters.isEmpty)
        #expect(NeuralNebulaLayout.place(zones: summary, nodes: [node("focused")], edges: [],
                                         regionType: nil).nodeCenters.count == 1)
        #expect(MemoryLayout<NeuralNebulaVertex>.stride == 48)
    }

    @Test func neuralNebulaDrawsOnlyRecordedLinksAndBoundsDecorativeLight() {
        let zones = GraphZoneOverview(entityCount: 7, relationshipCount: 3, zones: [
            GraphTypeCount(entityType: "project", count: 5),
            GraphTypeCount(entityType: "concept", count: 2)
        ], links: [
            GraphZoneLink(subjectType: "project", objectType: "concept",
                          confirmedCount: 1, suggestedCount: 1, contradictedCount: 1)
        ])
        let nodes = [node("first", type: "project"), node("second", type: "concept")]
        let edges = [edge("one", "first", "second")]
        let placement = NeuralNebulaLayout.place(zones: zones, nodes: nodes,
                                                 edges: edges, regionType: "project")
        let geometry = NeuralNebulaGeometry.build(overview: zones, placement: placement,
                                                  nodes: nodes, edges: edges,
                                                  selectedNodeId: nil, highContrast: false)
        #expect(geometry.zoneLines.count == 6)
        #expect(geometry.detailLines.count == 2)
        #expect(geometry.detailPoints.count == 2)
        #expect(geometry.zonePoints.count <= 2 * 73 + 220)
        let disconnected = NeuralNebulaGeometry.build(
            overview: GraphZoneOverview(entityCount: 7, relationshipCount: 0,
                                        zones: zones.zones, links: []),
            placement: placement, nodes: nodes, edges: [],
            selectedNodeId: nil, highContrast: false)
        #expect(disconnected.zoneLines.isEmpty)
        #expect(disconnected.detailLines.isEmpty)
    }

    @Test func neuralNebulaCameraInterpolatesInsteadOfTeleportingBetweenLevels() {
        var camera = NeuralNebulaCamera()
        camera.desiredCenter = SIMD3<Float>(180, 42, -55)
        camera.desiredDistance = 157
        let before = camera.projection(aspect: 1.5)
        camera.settle(weight: 0.14)
        #expect(camera.distance < 590 && camera.distance > 157)
        #expect(camera.center != camera.desiredCenter)
        camera.orbit(dx: 12, dy: -6)
        let after = camera.projection(aspect: 1.5)
        #expect(before != after)
        camera.settle(weight: 1)
        #expect(camera.center == camera.desiredCenter)
        #expect(camera.distance == 157)
    }

    @Test func globalAtlasLayoutIsStableAndKeepsRealComponentsSeparate() {
        let nodes = [node("a"), node("b"), node("c"), node("d"), node("isolated")]
        let edges = [edge("one", "a", "b"), edge("two", "b", "c"), edge("three", "c", "d")]
        let first = GraphAtlasLayout.positions(nodes: nodes, edges: edges)
        let second = GraphAtlasLayout.positions(nodes: Array(nodes.reversed()), edges: Array(edges.reversed()))
        #expect(first == second)
        #expect(first.count == 5)
        #expect(first.values.allSatisfy { $0.x.isFinite && $0.y.isFinite })
        #expect(first["isolated"] != first["b"])
        #expect(first["a"] != first["c"])
    }

    @Test func workerGraphEnvelopeDecodesWithoutInventingMetadata() throws {
        let json = """
            {"outcome":"ready","focusId":"a","nextCursor":null,
             "nodes":[{"entityId":"a","label":"Project","entityType":"project","typeOrigin":"owner",
                       "status":"confirmed","mentions":2,"sources":1,"typeConflict":false,
                       "evidence":null,"actions":["openEntity"]}],
             "edges":[]}
            """
        let page = try JSONDecoder().decode(GraphPage.self, from: Data(json.utf8))
        #expect(page.nodes.count == 1)
        #expect(page.nodes[0].label == "Project")
        #expect(page.nodes[0].evidence == nil)
        #expect(page.edges.isEmpty)
    }

    @Test func projectionNeverAddsAnEdgeWithoutBothCurrentEndpoints() {
        let page = GraphPage(outcome: "ready", focusId: "a", nodes: [node("a"), node("b")],
                             edges: [edge("ok", "a", "b"), edge("hidden", "a", "secret")], nextCursor: nil)
        let result = GraphProjection.merge(nodes: [], edges: [], page: page)
        #expect(result.nodes.count == 2)
        #expect(result.edges.map(\.relationId) == ["ok"])
    }

    @Test func distinctObservationsRemainDistinctIncludingAContradictedVerdict() {
        let observations = [
            edge("first", "a", "b"),
            GraphEdge(relationId: "second", subjectId: "a", objectId: "b",
                      predicate: "references", origin: "model", evidenceStatus: "contradicted",
                      evidence: nil, actions: ["inspectRelation"])
        ]
        let result = GraphProjection.merge(nodes: [], edges: [],
                                           page: GraphPage(outcome: "ready", focusId: "a",
                                                           nodes: [node("a"), node("b")],
                                                           edges: observations, nextCursor: nil))
        #expect(result.edges.map(\.relationId) == ["first", "second"])
        #expect(result.edges.map(\.evidenceStatus) == ["owner", "contradicted"])
    }

    @Test func evidencePreviewAndReviewUseTheExistingTypedResponses() throws {
        let preview = try JSONDecoder().decode(GraphPreview.self,
            from: Data(#"{"outcome":"completed","content":"Example source text","reasonCode":null}"#.utf8))
        let decision = try JSONDecoder().decode(GraphReviewOutcome.self,
            from: Data(#"{"reviewed":false}"#.utf8))
        #expect(preview.content == "Example source text")
        #expect(decision.reviewed == false)
    }

    @Test func sourceJumpRequiresExactActiveSourceRevisionAndDigest() {
        let evidence = GraphEvidence(proposalId: "p", sourceId: "s", itemId: "i",
                                     revision: 3, sourceDigest: "digest", locator: "file.md",
                                     text: "quoted", offset: 0, status: "accepted", verdict: nil)
        let current = LibraryItem(sourceId: "s", itemId: "i", sourceKind: "file",
                                  sourceLocator: "file.md", sourceStatus: "active",
                                  revision: 3, sha256: "digest", originalBytes: 42,
                                  processingState: nil, extractionOutcome: nil)
        #expect(evidence.matchesCurrent(current))
        let revoked = LibraryItem(sourceId: "s", itemId: "i", sourceKind: "file",
                                  sourceLocator: "file.md", sourceStatus: "revoked",
                                  revision: 3, sha256: "digest", originalBytes: 42,
                                  processingState: nil, extractionOutcome: nil)
        let revised = LibraryItem(sourceId: "s", itemId: "i", sourceKind: "file",
                                  sourceLocator: "file.md", sourceStatus: "active",
                                  revision: 4, sha256: "new-digest", originalBytes: 42,
                                  processingState: nil, extractionOutcome: nil)
        #expect(!evidence.matchesCurrent(revoked))
        #expect(!evidence.matchesCurrent(revised))
        let other = LibraryItem(sourceId: "other", itemId: "i", sourceKind: "file",
                                sourceLocator: "file.md", sourceStatus: "active",
                                revision: 3, sha256: "digest", originalBytes: 42,
                                processingState: nil, extractionOutcome: nil)
        #expect(!evidence.matchesCurrent(other))
    }

    @Test func evidenceExcerptRequiresTheExactCurrentUTF16Offset() {
        let content = "😀 C3Reader parses PHY."
        let evidence = GraphEvidence(proposalId: "p", sourceId: "s", itemId: "i",
                                     revision: 1, sourceDigest: "sha", locator: "reader.md",
                                     text: "C3Reader parses PHY.", offset: 3,
                                     status: "accepted", verdict: nil)
        #expect(evidence.excerpt(in: content) == content)

        let wrongOffset = GraphEvidence(proposalId: "p", sourceId: "s", itemId: "i",
                                        revision: 1, sourceDigest: "sha", locator: "reader.md",
                                        text: "C3Reader parses PHY.", offset: 2,
                                        status: "accepted", verdict: nil)
        #expect(wrongOffset.excerpt(in: content) == nil)
        #expect(evidence.excerpt(in: "C3Reader parses PHY.") == nil)
    }

    @Test func repeatedPageIsIdempotentAndStable() {
        let page = GraphPage(outcome: "ready", focusId: "a", nodes: [node("b"), node("a")],
                             edges: [edge("r", "a", "b")], nextCursor: nil)
        let first = GraphProjection.merge(nodes: [], edges: [], page: page)
        let second = GraphProjection.merge(nodes: first.nodes, edges: first.edges, page: page)
        #expect(second.nodes == first.nodes)
        #expect(second.edges == first.edges)
        #expect(second.nodes.map(\.entityId) == ["a", "b"])
    }

    @Test func sceneBudgetDropsOverLimitWithoutCreatingMissingEdges() {
        let nodes = (0..<130).map { node("n\($0)") }
        let edges = (0..<130).map { edge("r\($0)", "n0", "n\($0)") }
        let result = GraphProjection.merge(nodes: [], edges: [],
                                           page: GraphPage(outcome: "ready", focusId: "n0",
                                                           nodes: nodes, edges: edges, nextCursor: nil))
        #expect(result.nodes.count == GraphBudget.maximumNodes)
        #expect(result.edges.count <= GraphBudget.maximumEdges)
        #expect(result.edges.allSatisfy { edge in
            result.nodes.contains(where: { $0.entityId == edge.subjectId }) &&
            result.nodes.contains(where: { $0.entityId == edge.objectId })
        })
    }

    @Test func layoutIsDeterministicAndDoesNotInventRelations() {
        let nodes = [node("a", type: "person"), node("b"), node("c")]
        var one = GraphLayout()
        var two = GraphLayout()
        one.configure(nodes: nodes, focus: "a")
        two.configure(nodes: nodes, focus: "a")
        #expect(one.positions == two.positions)
        #expect(one.positions["a"] == CGPoint.zero)
        for _ in 0..<40 {
            _ = one.advance(nodes: nodes, edges: [edge("r", "a", "b")], focus: "a",
                            reducedMotion: false)
        }
        #expect(one.positions["a"] == CGPoint.zero)
        #expect(one.positions.values.allSatisfy { $0.x.isFinite && $0.y.isFinite })
        #expect(two.positions["a"] == CGPoint.zero)
    }

    @MainActor
    @Test func spriteKitSceneCanPopulateResizeSelectAndClear() {
        let scene = GraphScene()
        let nodes = [node("a"), node("b", type: "project")]
        scene.configure(nodes: nodes, edges: [edge("r", "a", "b")], focus: "a")
        scene.size = CGSize(width: 460, height: 320)
        scene.updateAppearance(dark: false, highContrast: true)
        scene.setSelection(node: "a", edge: nil)
        #expect(scene.nodes(at: .zero).contains { $0.name == "node:a" })

        scene.configure(nodes: [], edges: [], focus: nil)
        scene.size = CGSize(width: 900, height: 700)
        #expect(!scene.children.contains { $0.name == "node:a" })
    }

    @Test func reducedMotionPreservesStaticPositionsAndManualDragWorks() {
        let nodes = [node("a"), node("b")]
        var layout = GraphLayout()
        layout.configure(nodes: nodes, focus: "a")
        let before = layout.positions
        let didAdvance = layout.advance(nodes: nodes, edges: [edge("r", "a", "b")],
                                        focus: "a", reducedMotion: true)
        #expect(!didAdvance)
        #expect(layout.positions == before)
        layout.move("b", to: CGPoint(x: 50, y: 25))
        #expect(layout.positions["b"] == CGPoint(x: 50, y: 25))
    }

    @MainActor
    @Test func globalAtlasBatchesDrawablesAndKeepsCameraInteractionsBounded() {
        let nodes = (0..<GraphBudget.atlasNodes).map { node("atlas-\($0)", type: "project") }
        let edges = (0..<GraphBudget.atlasEdges).map { index in
            edge("edge-\(index)", "atlas-\(index)", "atlas-\(index + 1)")
        }
        let scene = GraphScene()
        scene.configure(nodes: nodes, edges: edges, focus: nil)
        let drawingLayers = scene.children.compactMap { $0 as? SKShapeNode }
        let labels = scene.children.compactMap { $0 as? SKLabelNode }

        #expect(drawingLayers.count < 24)
        #expect(labels.count == 1)
        #expect(scene.camera?.xScale == scene.camera?.yScale)

        let initialScale = scene.camera?.xScale ?? 0
        scene.zoom(by: 0.6, at: .zero)
        #expect((scene.camera?.xScale ?? 0) < initialScale)
        scene.rotate(by: .pi / 4)
        #expect(abs((scene.camera?.zRotation ?? 0) - .pi / 4) < 0.001)
        scene.reduceMotion = true
    }

}
