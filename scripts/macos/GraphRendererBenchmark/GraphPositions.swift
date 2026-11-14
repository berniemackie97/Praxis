import CoreGraphics
import Foundation

enum GraphPositions {
    static func layout(_ nodes: [GraphFixture.Node], in size: CGSize, at time: TimeInterval,
                       animated: Bool) -> [CGPoint] {
        nodes.enumerated().map { index, node in
            let shiftX = animated ? 5 * sin(time * 0.7 + Double(index) * 0.19) : 0
            let shiftY = animated ? 5 * cos(time * 0.7 + Double(index) * 0.13) : 0
            return CGPoint(x: (node.seedX + shiftX) * size.width / 1000,
                           y: (node.seedY + shiftY) * size.height / 1000)
        }
    }
}
