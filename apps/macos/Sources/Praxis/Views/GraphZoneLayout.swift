import CoreGraphics
import Foundation

struct GraphZonePlacement {
    let positions: [String: CGPoint]
    let radii: [String: CGFloat]
    let bounds: CGRect
}

enum GraphZoneLayout {
    private struct RegionPair: Hashable {
        let first: String
        let second: String

        init(_ a: String, _ b: String) {
            first = min(a, b)
            second = max(a, b)
        }
    }

    static func place(_ overview: GraphVisualOverview) -> GraphZonePlacement {
        let regions = overview.regions.filter { $0.count > 0 }.sorted {
            $0.count == $1.count ? $0.id < $1.id : $0.count > $1.count
        }
        guard !regions.isEmpty else {
            return GraphZonePlacement(positions: [:], radii: [:], bounds: .zero)
        }
        let goldenAngle = CGFloat.pi * (3 - sqrt(CGFloat(5)))
        let radii = Dictionary(uniqueKeysWithValues: regions.map {
            ($0.id, min(90, max(30, 24 + sqrt(CGFloat($0.count)) * 5)))
        })
        var points = regions.enumerated().map { index, _ in
            let radius = CGFloat(index) * 57
            let theta = CGFloat(index) * goldenAngle
            return CGPoint(x: cos(theta) * radius, y: sin(theta) * radius)
        }
        var weights: [RegionPair: Int] = [:]
        for link in overview.links where link.count > 0 && link.subjectId != link.objectId {
            let key = RegionPair(link.subjectId, link.objectId)
            weights[key, default: 0] += link.count
        }
        for _ in 0..<90 {
            var impulses = Array(repeating: CGVector.zero, count: regions.count)
            for first in 0..<regions.count {
                for second in (first + 1)..<regions.count {
                    let dx = points[second].x - points[first].x
                    let dy = points[second].y - points[first].y
                    let distance = max(1, hypot(dx, dy))
                    let spacing = (radii[regions[first].id] ?? 30) +
                        (radii[regions[second].id] ?? 30) + 32
                    let key = RegionPair(regions[first].id, regions[second].id)
                    let weight = CGFloat(weights[key, default: 0])
                    let repel = max(0, spacing - distance) * 0.22
                    let attract = weight == 0 ? CGFloat(0) :
                        min(2.2, sqrt(weight) * 0.14) * max(0, distance - spacing - 65) * 0.015
                    let delta = repel - attract
                    let x = dx / distance * delta
                    let y = dy / distance * delta
                    impulses[first].dx -= x
                    impulses[first].dy -= y
                    impulses[second].dx += x
                    impulses[second].dy += y
                }
            }
            for index in points.indices {
                points[index].x = (points[index].x + impulses[index].dx) * 0.999
                points[index].y = (points[index].y + impulses[index].dy) * 0.999
            }
        }
        let positions = Dictionary(uniqueKeysWithValues: zip(regions.map(\.id), points))
        let minX = regions.indices.map { points[$0].x - (radii[regions[$0].id] ?? 30) }.min() ?? 0
        let maxX = regions.indices.map { points[$0].x + (radii[regions[$0].id] ?? 30) }.max() ?? 0
        let minY = regions.indices.map { points[$0].y - (radii[regions[$0].id] ?? 30) }.min() ?? 0
        let maxY = regions.indices.map { points[$0].y + (radii[regions[$0].id] ?? 30) }.max() ?? 0
        return GraphZonePlacement(positions: positions, radii: radii,
                                  bounds: CGRect(x: minX - 50, y: minY - 50,
                                                 width: maxX - minX + 100, height: maxY - minY + 100))
    }
}
