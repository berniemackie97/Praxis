import SwiftUI

/// A scoped entry into the single knowledge catalog; the animated graph is a focused drill-down.
struct KnowledgeOverviewView: View {
    let overview: GraphOverview?
    let category: String
    let scopeDescription: String
    let chooseCategory: (String) -> Void

    private let columns = [GridItem(.adaptive(minimum: 180, maximum: 260), spacing: Spacing.m)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.xl) {
                VStack(alignment: .leading, spacing: Spacing.s) {
                    Text("Your knowledge").font(.largeTitle.weight(.semibold))
                    Text("One catalog across your sources. Explore a category or search for an entity, then open its connected neighborhood.")
                        .font(.callout).foregroundStyle(.secondary)
                    Text(scopeDescription).font(.caption).foregroundStyle(.secondary)
                }

                if let overview {
                    VStack(alignment: .leading, spacing: Spacing.m) {
                        Text("\(overview.entityCount.formatted()) current entities")
                            .font(.title2.weight(.semibold))
                        Text("Counts include only visible entities in the selected source and collection scope. Suggestions appear only when enabled.")
                            .font(.caption).foregroundStyle(.secondary)

                        if overview.types.isEmpty {
                            EmptyState(systemImage: "tray", title: "No visible knowledge in this scope",
                                       message: "Add or approve source evidence, or broaden the selected source and collection.")
                        } else {
                            LazyVGrid(columns: columns, alignment: .leading, spacing: Spacing.m) {
                                ForEach(overview.types) { group in
                                    Button { chooseCategory(group.entityType) } label: {
                                        HStack(spacing: Spacing.m) {
                                            Image(systemName: EntitySymbol.of(group.entityType))
                                                .font(.title3).frame(width: 28)
                                                .foregroundStyle(Color.accentColor)
                                            VStack(alignment: .leading, spacing: Spacing.xxs) {
                                                Text(KnowledgeVocabulary.label(group.entityType).capitalized)
                                                    .font(.headline)
                                                Text(Count.label(group.count, "entity"))
                                                    .font(.caption).foregroundStyle(.secondary)
                                            }
                                            Spacer(minLength: 0)
                                        }
                                        .padding(Spacing.m)
                                        .frame(maxWidth: .infinity, minHeight: 68, alignment: .leading)
                                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: Radius.m))
                                        .overlay(RoundedRectangle(cornerRadius: Radius.m)
                                            .stroke(category == group.entityType ? Color.accentColor : .clear, lineWidth: 1))
                                    }
                                    .buttonStyle(.plain)
                                    .accessibilityLabel("Explore \(KnowledgeVocabulary.label(group.entityType)), \(Count.label(group.count, "entity"))")
                                }
                            }
                        }
                    }
                    if !category.isEmpty {
                        Button("Show all types") { chooseCategory("") }
                    }
                } else {
                    ProgressView("Reading current knowledge")
                }

                VStack(alignment: .leading, spacing: Spacing.s) {
                    Text("Explore connected evidence").font(.headline)
                    Text("Choose a result from the left to open a bounded network around it. That network is one view of this same knowledge catalog, not a separate brain.")
                        .font(.callout).foregroundStyle(.secondary)
                    Text("Some sources may still be processing. This overview counts current available knowledge, not queued intake.")
                        .font(.caption).foregroundStyle(.tertiary)
                }
            }
            .padding(Spacing.xl)
            .frame(maxWidth: 1100, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
    }
}
