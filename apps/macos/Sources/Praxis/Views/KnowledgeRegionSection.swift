import SwiftUI

/// The territories every brain style draws, as an accessible list: the same regions, counts and
/// forming state, and the same way in (selecting one enters it in the graph).
struct KnowledgeRegionSection: View {
    @ObservedObject var graph: GraphWorkbenchModel

    var body: some View {
        let territories = graph.territories
        if !territories.isEmpty || graph.regionMapLoading || graph.regionMapError != nil {
            Divider()
            HStack {
                Text("Regions").font(.subheadline.weight(.semibold))
                Spacer()
                if graph.regionMapLoading { ProgressView().controlSize(.small) }
            }
            if let error = graph.regionMapError {
                Text("Regions unavailable: \(error)").font(.caption2).foregroundStyle(.orange)
            } else {
                Text("Derived from your folders and repositories · evolving")
                    .font(.caption2).foregroundStyle(.secondary)
                List(territories) { region in
                    Button { graph.openRegion(region.regionId) } label: {
                        HStack(spacing: Spacing.s) {
                            Circle()
                                .strokeBorder(region.forming ? Color.secondary : .clear, style: StrokeStyle(lineWidth: 1, dash: [2, 2]))
                                .background(Circle().fill(region.forming ? Color.clear : BrainPalette.color(for: region, in: graph.regionMap)))
                                .frame(width: 9, height: 9)
                            VStack(alignment: .leading, spacing: Spacing.xs) {
                                Text(region.label).lineLimit(1)
                                Text(detail(region)).font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                    }
                    .buttonStyle(.plain)
                    .padding(.vertical, 2)
                    .background(graph.openRegionId == region.regionId ? Color.accentColor.opacity(0.15) : .clear,
                                in: RoundedRectangle(cornerRadius: Radius.s))
                    .accessibilityLabel("Enter \(region.label), \(detail(region))")
                }
                .frame(minHeight: 90, maxHeight: 260)
                .listStyle(.plain)
                if let map = graph.regionMap, map.unplacedEntities > 0 {
                    Text("\(map.unplacedEntities.formatted()) entities come from sources outside any watched folder.")
                        .font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func detail(_ region: GraphKnowledgeRegion) -> String {
        if region.forming { return "forming · \(region.pendingSources.formatted()) sources queued" }
        var parts = ["\(region.entities.formatted()) entities", "\(region.sources.formatted()) sources"]
        if region.pendingSources > 0 { parts.append("\(region.pendingSources.formatted()) queued") }
        return parts.joined(separator: " · ")
    }
}

/// Region colours shared by every brain style: a territory takes its own hue, forming ones are grey.
enum BrainPalette {
    static let hues: [SIMD3<Float>] = [
        [1.00, 0.36, 0.36], [1.00, 0.66, 0.30], [1.00, 0.88, 0.40], [0.97, 0.51, 0.67],
        [0.69, 0.59, 0.99], [0.45, 0.75, 0.99], [0.39, 0.90, 0.75], [1.00, 0.79, 0.66],
        [0.40, 0.85, 0.91], [0.66, 0.89, 0.29], [0.95, 0.55, 0.95], [0.55, 0.95, 0.60]
    ]
    static let forming: SIMD3<Float> = [0.60, 0.63, 0.65]

    static func rgb(for region: GraphKnowledgeRegion, in map: GraphRegionMap?) -> SIMD3<Float> {
        if region.forming { return forming }
        let territories = BrainTerritories.select(map)
        let index = territories.firstIndex(where: { $0.regionId == region.regionId })
            ?? Int(GraphRegionIdentity.hash(region.regionId) % UInt64(hues.count))
        return hues[index % hues.count]
    }

    static func color(for region: GraphKnowledgeRegion, in map: GraphRegionMap?) -> Color {
        let value = rgb(for: region, in: map)
        return Color(red: Double(value.x), green: Double(value.y), blue: Double(value.z))
    }
}
