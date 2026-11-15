import SwiftUI

struct GroundedProjectSection: View {
    @ObservedObject var graph: GraphWorkbenchModel

    var body: some View {
        if graph.conceptRegionCount > 0 || graph.conceptLoading {
            Divider()
            HStack {
                Text("Grounded projects").font(.subheadline.weight(.semibold))
                Spacer()
                if graph.conceptLoading { ProgressView().controlSize(.small) }
            }
            Text("\(graph.conceptRegions.count) of \(graph.conceptRegionCount) confirmed project anchors")
                .font(.caption2).foregroundStyle(.secondary)
            List {
                ForEach(graph.conceptRegions) { region in
                    Button { graph.focusEntityFromBrowse(region.regionId) } label: {
                        VStack(alignment: .leading, spacing: Spacing.xs) {
                            Text(region.label).lineLimit(1)
                            Text("\(region.containedEntityCount) explicit members · " +
                                 "\(region.relatedEntityCount) related entities")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .accessibilityLabel("Explore project \(region.label), " +
                                        "\(region.containedEntityCount) explicit members")
                }
            }
            .frame(minHeight: 70, maxHeight: 180)
            .listStyle(.plain)
            if graph.conceptNextCursor != nil {
                Button("More project anchors") { graph.loadMoreConcepts() }
                    .disabled(graph.conceptLoading)
            }
        }
        if let conceptError = graph.conceptError {
            Text("Project anchors unavailable: \(conceptError)")
                .font(.caption2).foregroundStyle(.orange)
        }
    }
}
