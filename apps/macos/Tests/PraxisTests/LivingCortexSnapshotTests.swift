import AppKit
import Foundation
import Metal
import SpriteKit
import Testing
@testable import Praxis

/// Renders Living Cortex offscreen (Metal views cannot be captured from layers) at the whole-brain
/// and territory tiers, checks the scene actually draws, and writes PNGs for visual review when
/// `PRAXIS_SNAPSHOT_DIR` is set. `PRAXIS_CORTEX_MAP_JSON` (and optionally
/// `PRAXIS_CORTEX_MEMBERS_JSON`) render a locally exported region map instead of the synthetic one.
@MainActor
@Suite("Brain style rendering")
struct LivingCortexSnapshotTests {
    @Test("Whole brain and an entered territory render with visible structure")
    func renders() throws {
        let environment = ProcessInfo.processInfo.environment
        let decoder = JSONDecoder()
        let map = try environment["PRAXIS_CORTEX_MAP_JSON"].map { try decoder.decode(GraphRegionMap.self, from: Data(contentsOf: URL(fileURLWithPath: $0))) }
            ?? decoder.decode(GraphRegionMap.self, from: Data(Self.syntheticMap.utf8))
        let members = try environment["PRAXIS_CORTEX_MEMBERS_JSON"].map { try decoder.decode(GraphPage.self, from: Data(contentsOf: URL(fileURLWithPath: $0))) }
            ?? decoder.decode(GraphPage.self, from: Data(Self.syntheticMembers.utf8))
        guard let renderer = LivingCortexRenderer(view: nil) else { return }
        let territories = BrainTerritories.select(map)
        let links = BrainTerritories.links(map, shown: territories)
        #expect(territories.count >= 8)
        #expect(!links.isEmpty)

        renderer.update(regions: territories, map: map, links: links, openId: nil, nodes: [], edges: [],
                        selectedNodeId: nil, selectedEdgeId: nil, reduceMotion: true, highContrast: false, active: false)
        let size = CGSize(width: 1440, height: 900)
        let whole = try #require(renderer.snapshot(size: size))
        #expect(Self.litFraction(whole) > 0.03)

        let open = territories[0].regionId
        renderer.update(regions: territories, map: map, links: links, openId: open, nodes: members.nodes, edges: members.edges,
                        selectedNodeId: nil, selectedEdgeId: nil, reduceMotion: true, highContrast: false, active: false)
        let territory = try #require(renderer.snapshot(size: size))
        #expect(Self.litFraction(territory) > 0.01)

        // Knowledge Cosmos draws the same territories as galaxies.
        var universe: CGImage?, galaxy: CGImage?, system: CGImage?
        if let cosmos = NeuralNebulaRenderer(view: nil) {
            cosmos.update(overview: BrainTerritories.overview(map), nodes: [], edges: [], regionType: nil, focusId: nil,
                          selectedNodeId: nil, selectedEdgeId: nil, reduceMotion: true, highContrast: false, active: false)
            universe = cosmos.snapshot(size: size)
            #expect(universe.map(Self.litFraction) ?? 0 > 0.01)
            // Entering a galaxy flies into its disc; selecting a star opens its planetary system.
            cosmos.update(overview: BrainTerritories.overview(map), nodes: members.nodes, edges: members.edges,
                          regionType: open, focusId: nil, selectedNodeId: nil, selectedEdgeId: nil,
                          reduceMotion: true, highContrast: false, active: false)
            galaxy = cosmos.snapshot(size: size)
            #expect(galaxy.map(Self.litFraction) ?? 0 > 0.01)
            let star = members.edges.map(\.subjectId).first
            cosmos.update(overview: BrainTerritories.overview(map), nodes: members.nodes, edges: members.edges,
                          regionType: open, focusId: nil, selectedNodeId: star, selectedEdgeId: nil,
                          reduceMotion: true, highContrast: false, active: false)
            system = cosmos.snapshot(size: size)
            #expect(system.map(Self.litFraction) ?? 0 > 0.005)
        }

        // Cellular Atlas draws them as cells (SpriteKit, rendered offscreen with SKRenderer).
        let tissue = Self.cells(BrainTerritories.overview(map), size: size)
        #expect(tissue.map(Self.litFraction) ?? 0 > 0.01)
        let interior = Self.cells(BrainTerritories.overview(map), size: size, entering: territories[0].regionId,
                                  nodes: members.nodes, edges: members.edges)
        #expect(interior.map(Self.litFraction) ?? 0 > 0.01)

        if let output = environment["PRAXIS_SNAPSHOT_DIR"] {
            let folder = URL(fileURLWithPath: output, isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try Self.png(whole).write(to: folder.appendingPathComponent("living-cortex-whole.png"))
            try Self.png(territory).write(to: folder.appendingPathComponent("living-cortex-territory.png"))
            if let universe { try Self.png(universe).write(to: folder.appendingPathComponent("knowledge-cosmos-universe.png")) }
            if let galaxy { try Self.png(galaxy).write(to: folder.appendingPathComponent("knowledge-cosmos-galaxy.png")) }
            if let system { try Self.png(system).write(to: folder.appendingPathComponent("knowledge-cosmos-system.png")) }
            if let tissue { try Self.png(tissue).write(to: folder.appendingPathComponent("cellular-atlas-tissue.png")) }
            if let interior { try Self.png(interior).write(to: folder.appendingPathComponent("cellular-atlas-interior.png")) }
        }
    }

    private static func cells(_ overview: GraphVisualOverview, size: CGSize, entering: String? = nil,
                              nodes: [GraphNode] = [], edges: [GraphEdge] = []) -> CGImage? {
        let scene = GraphScene()
        scene.size = size
        scene.reduceMotion = true
        scene.updateAppearance(dark: true, highContrast: false, force: true)
        let view = SKView(frame: CGRect(origin: .zero, size: size))
        view.presentScene(scene)
        scene.configureZones(overview)
        if let entering {
            scene.interiorTint = NSColor(calibratedRed: 1, green: 0.36, blue: 0.36, alpha: 1)
            scene.configure(nodes: nodes, edges: edges, focus: nil, regionType: entering)
        }
        guard let texture = view.texture(from: scene) else { return nil }
        let art = texture.cgImage()
        // Composite onto the scene's background, the way the view shows it.
        let width = Int(size.width), height = Int(size.height)
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        context.setFillColor(CGColor(red: 0.01, green: 0.035, blue: 0.075, alpha: 1))
        context.fill(CGRect(origin: .zero, size: size))
        let scale = min(size.width * 0.92 / CGFloat(art.width), size.height * 0.92 / CGFloat(art.height))
        let drawn = CGSize(width: CGFloat(art.width) * scale, height: CGFloat(art.height) * scale)
        context.draw(art, in: CGRect(x: (size.width - drawn.width) / 2, y: (size.height - drawn.height) / 2,
                                     width: drawn.width, height: drawn.height))
        return context.makeImage()
    }

    private static func litFraction(_ image: CGImage) -> Double {
        guard let data = image.dataProvider?.data, let bytes = CFDataGetBytePtr(data) else { return 0 }
        let pixels = image.width * image.height
        var lit = 0
        for index in stride(from: 0, to: pixels * 4, by: 4 * 7) where Int(bytes[index]) + Int(bytes[index + 1]) + Int(bytes[index + 2]) > 90 {
            lit += 1
        }
        return Double(lit) / Double(pixels / 7)
    }

    private static func png(_ image: CGImage) throws -> Data {
        let representation = NSBitmapImageRep(cgImage: image)
        return try #require(representation.representation(using: .png, properties: [:]))
    }

    private static func node(_ id: Int, _ label: String, mentions: Int) -> String {
        """
        {"entityId":"00000000-0000-0000-0000-\(String(format: "%012d", id))","label":"\(label)","entityType":"concept",
         "typeOrigin":"inferred","status":"confirmed","mentions":\(mentions),"sources":1,"typeConflict":false,"evidence":null,"actions":[]}
        """
    }

    private static func region(_ id: String, parent: String?, _ label: String, kind: String, entities: Int, pending: Int = 0) -> String {
        """
        {"regionId":"\(id)","parentId":\(parent.map { "\"\($0)\"" } ?? "null"),"label":"\(label)","kind":"\(kind)","summary":null,
         "entities":\(entities),"sources":\(max(1, entities / 4)),"pendingSources":\(pending),"relations":\(entities / 5),"hubs":[]}
        """
    }

    /// A synthetic brain shaped like a real one: one dominant project group, a forming folder and loose notes.
    static let syntheticMap: String = {
        let id = { (n: Int) in String(format: "%032x", n) }
        let themes = ["Rendering", "World Map", "Network", "Architecture", "Server", "Entities", "Crypto", "Audio", "Tooling", "Archive"]
        var regions = [region(id(1), parent: nil, "Harbor Engine", kind: "group", entities: 3400),
                       region(id(2), parent: id(1), "notes", kind: "project", entities: 2400),
                       region(id(3), parent: id(1), "HarborClient", kind: "project", entities: 800),
                       region(id(4), parent: id(1), "HarborServer", kind: "folder", entities: 430),
                       region(id(5), parent: nil, "household planning", kind: "folder", entities: 0, pending: 290),
                       region(id(6), parent: nil, "Loose files in Inbox", kind: "loose", entities: 34)]
        for (index, theme) in themes.enumerated() {
            regions.append(region(id(10 + index), parent: id(2), theme, kind: "folder", entities: max(18, 1080 / (index + 1))))
        }
        let pairs = [(10, 14), (11, 14), (12, 14), (10, 11), (12, 13), (13, 3), (11, 3), (10, 4), (15, 16)]
        let links = pairs.map { "{\"fromRegionId\":\"\(id($0.0))\",\"toRegionId\":\"\(id($0.1))\",\"relations\":\(5 + $0.0),\"sharedEntities\":0}" }
        return "{\"revision\":7,\"entities\":3500,\"unplacedEntities\":0,\"regions\":[\(regions.joined(separator: ","))],\"links\":[\(links.joined(separator: ","))]}"
    }()

    static let syntheticMembers: String = {
        let nodes = (1...40).map { node($0, "Concept \($0)", mentions: 40 - $0) }
        let edges = (1...30).map { index -> String in
            let subject = String(format: "00000000-0000-0000-0000-%012d", index)
            let object = String(format: "00000000-0000-0000-0000-%012d", (index * 7) % 40 + 1)
            return """
            {"relationId":"10000000-0000-0000-0000-\(String(format: "%012d", index))","subjectId":"\(subject)","objectId":"\(object)",
             "predicate":"references","origin":"model","evidenceStatus":"documented","evidence":null,"actions":[]}
            """
        }
        return "{\"outcome\":\"ready\",\"focusId\":null,\"nodes\":[\(nodes.joined(separator: ","))],\"edges\":[\(edges.joined(separator: ","))],\"nextCursor\":null}"
    }()
}
