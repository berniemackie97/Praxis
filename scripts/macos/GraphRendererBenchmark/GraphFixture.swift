import Foundation

struct GraphFixture: Decodable {
    struct Node: Decodable, Identifiable {
        let id: String
        let label: String
        let kind: String
        let seedX: Double
        let seedY: Double
    }

    struct Edge: Decodable {
        let sourceId: String
        let targetId: String
        let predicate: String
        let status: String
    }

    let nodes: [Node]
    let edges: [Edge]

    static func load(from path: String) throws -> GraphFixture {
        let url = URL(fileURLWithPath: path)
        let length = try FileManager.default.attributesOfItem(atPath: path)[.size] as? NSNumber
        guard let length, length.intValue <= 2_097_152 else { throw CocoaError(.fileReadTooLarge) }
        let fixture = try JSONDecoder().decode(GraphFixture.self, from: Data(contentsOf: url))
        guard fixture.nodes.count <= 2000, fixture.edges.count <= 4000 else {
            throw CocoaError(.fileReadTooLarge)
        }
        let ids = Set(fixture.nodes.map(\.id))
        guard ids.count == fixture.nodes.count,
              fixture.edges.allSatisfy({ ids.contains($0.sourceId) && ids.contains($0.targetId) }) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return fixture
    }
}
