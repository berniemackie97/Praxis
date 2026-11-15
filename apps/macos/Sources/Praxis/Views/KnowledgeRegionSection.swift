import SwiftUI

struct KnowledgeRegionSection: View {
    @ObservedObject var graph: GraphWorkbenchModel

    var body: some View {
        let nested = graph.isBrowsingConcept
        let regions = nested ? graph.childConcepts?.regions ?? [] : graph.conceptRegions
        let count = nested ? graph.childConcepts?.regionCount ?? 0 : graph.conceptRegionCount
        let loading = nested ? graph.childConceptLoading : graph.conceptLoading
        let error = nested ? graph.childConceptError : graph.conceptError
        let hasMore = (nested ? graph.childConcepts?.nextCursor : graph.conceptNextCursor) != nil

        if nested || count > 0 || loading || error != nil {
            Divider()
            HStack {
                Text(nested ? "Subregions" : "Knowledge regions").font(.subheadline.weight(.semibold))
                Spacer()
                if loading { ProgressView().controlSize(.small) }
            }
            if nested {
                Button { graph.backFromConcept() } label: { Label("Back", systemImage: "chevron.left") }
            }
            if let error {
                Text("Regions unavailable: \(error)").font(.caption2).foregroundStyle(.orange)
            } else if regions.isEmpty && nested && !loading {
                Text("No subregions. Explore direct connections in the graph.")
                    .font(.caption2).foregroundStyle(.secondary)
            } else if !regions.isEmpty {
                Text("\(regions.count) of \(count)")
                    .font(.caption2).foregroundStyle(.secondary)
                List(regions) { region in
                    Button { graph.openConcept(region.regionId) } label: {
                        VStack(alignment: .leading, spacing: Spacing.xs) {
                            Text(region.label).lineLimit(1)
                            Text("\(region.containedEntityCount) direct members")
                                .font(.caption2).foregroundStyle(.secondary)
                            Text(basis(region)).font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .accessibilityLabel("Explore \(region.label), \(region.containedEntityCount) direct members, \(basis(region))")
                }
                .frame(minHeight: 70, maxHeight: 180)
                .listStyle(.plain)
                if hasMore {
                    Button("More regions") { graph.loadMoreConcepts() }.disabled(loading)
                }
            }
        }
    }

    private func basis(_ region: GraphConceptRegion) -> String {
        var labels: [String] = []
        if region.ownerMemberCount > 0 { labels.append("Owner") }
        if region.acceptedMemberCount > 0 { labels.append("Accepted") }
        if region.verifiedMemberCount > 0 { labels.append("Verifier supported") }
        return labels.joined(separator: " · ")
    }
}
