import Foundation
import SpriteKit

@MainActor
@main
struct GraphSceneSmoke {
    static func main() throws {
        let paths = CommandLine.arguments.dropFirst()
        guard !paths.isEmpty else {
            throw CocoaError(.fileReadNoSuchFile)
        }

        for path in paths {
            let fixture = try GraphFixture.load(from: path)
            let scene = SpriteGraphView.GraphScene(fixture: fixture)
            scene.size = CGSize(width: 1100, height: 720)
            scene.updatePositions(at: 0)

            let expectedLabels = fixture.nodes.count <= 200 ? fixture.nodes.count : 0
            let expectedChildren = fixture.nodes.count + expectedLabels + 1
            guard scene.children.count == expectedChildren else {
                fatalError("Scene has \(scene.children.count) children; expected \(expectedChildren)")
            }
            print("SpriteKit startup passed: \(URL(fileURLWithPath: path).lastPathComponent)")
        }
    }
}
