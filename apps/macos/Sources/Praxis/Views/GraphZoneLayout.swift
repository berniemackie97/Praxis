import CoreGraphics
import Foundation

struct GraphZonePlacement {
    let positions: [String: CGPoint]
    let radii: [String: CGFloat]
    let bounds: CGRect
}

enum GraphZoneLayout {
    static func place(_ overview: GraphZoneOverview) -> GraphZonePlacement {
        let zones = overview.zones.filter { $0.count > 0 }.sorted {
            $0.count == $1.count ? $0.entityType < $1.entityType : $0.count > $1.count
        }
        guard !zones.isEmpty else {
            return GraphZonePlacement(positions: [:], radii: [:], bounds: .zero)
        }
        let goldenAngle = CGFloat.pi * (3 - sqrt(CGFloat(5)))
        let radii = Dictionary(uniqueKeysWithValues: zones.map {
            ($0.entityType, min(90, max(30, 24 + sqrt(CGFloat($0.count)) * 5)))
        })
        var points = zones.enumerated().map { index, zone in
            let radius = CGFloat(index) * 57
            let theta = CGFloat(index) * goldenAngle
            return CGPoint(x: cos(theta) * radius, y: sin(theta) * radius)
        }
        var weights: [String: Int] = [:]
        for link in overview.links where link.count > 0 && link.subjectType != link.objectType {
            let key = [link.subjectType, link.objectType].sorted().joined(separator: "\u{1F}")
            weights[key, default: 0] += link.count
        }
        for _ in 0..<90 {
            var impulses = Array(repeating: CGVector.zero, count: zones.count)
            for first in 0..<zones.count {
                for second in (first + 1)..<zones.count {
                    let dx = points[second].x - points[first].x
                    let dy = points[second].y - points[first].y
                    let distance = max(1, hypot(dx, dy))
                    let spacing = (radii[zones[first].entityType] ?? 30) +
                        (radii[zones[second].entityType] ?? 30) + 32
                    let key = [zones[first].entityType, zones[second].entityType].sorted()
                        .joined(separator: "\u{1F}")
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
        let positions = Dictionary(uniqueKeysWithValues: zip(zones.map(\.entityType), points))
        let minX = zones.indices.map { points[$0].x - (radii[zones[$0].entityType] ?? 30) }.min() ?? 0
        let maxX = zones.indices.map { points[$0].x + (radii[zones[$0].entityType] ?? 30) }.max() ?? 0
        let minY = zones.indices.map { points[$0].y - (radii[zones[$0].entityType] ?? 30) }.min() ?? 0
        let maxY = zones.indices.map { points[$0].y + (radii[zones[$0].entityType] ?? 30) }.max() ?? 0
        return GraphZonePlacement(positions: positions, radii: radii,
                                  bounds: CGRect(x: minX - 50, y: minY - 50,
                                                 width: maxX - minX + 100, height: maxY - minY + 100))
    }
}
