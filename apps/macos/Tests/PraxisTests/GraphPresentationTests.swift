import CoreGraphics
import Foundation
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
        #expect(one.positions["a"] == .zero)
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
        #expect(!layout.advance(nodes: nodes, edges: [edge("r", "a", "b")],
                                focus: "a", reducedMotion: true))
        #expect(layout.positions == before)
        layout.move("b", to: CGPoint(x: 50, y: 25))
        #expect(layout.positions["b"] == CGPoint(x: 50, y: 25))
    }
}
