import SwiftUI

/// Entities across all captured sources, with type, evidence counts and conflicts.
struct KnowledgeView: View {
    @ObservedObject var model: KnowledgeWorkbenchModel
    let verification: VerificationWorkbenchModel

    var body: some View {
        MasterDetail(listWidth: (260, 320, 440)) {
            List(selection: Binding<String?>(
                get: { model.selected?.summary.entityId },
                set: { id in if let entity = model.entities.first(where: { $0.entityId == id }) { model.select(entity) } })) {
                ForEach(model.entities) { entity in
                    ItemRow(systemImage: EntitySymbol.of(entity.entityType), title: entity.name,
                            subtitle: "\(KnowledgeVocabulary.label(entity.entityType).capitalized) · \(Count.label(entity.sources, "source"))",
                            tint: entity.status == "rejected" ? .secondary : .accentColor) {
                        if entity.typeConflict {
                            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                                .help("Conflicting types").accessibilityLabel("Conflicting types")
                        }
                        Text("\(entity.mentions)").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                            .help(Count.label(entity.mentions, "mention"))
                    }
                    .tag(entity.entityId)
                }
            }
            .overlay {
                if model.entities.isEmpty {
                    EmptyState(systemImage: "point.3.connected.trianglepath.dotted", title: "No entities yet",
                               message: model.search.isEmpty && model.typeFilter.isEmpty
                                   ? "People, projects and ideas appear once the local model has read your captured text."
                                   : "Nothing matches this filter.")
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) {
                if model.error != nil || model.notice != nil {
                    FeedbackLine(error: model.error, notice: model.notice).padding(Spacing.s)
                }
            }
        } detail: {
            if let detail = model.selected {
                EntityDetailView(detail: detail, model: model, verification: verification)
            } else {
                EmptyState(systemImage: "person.crop.rectangle.stack", title: "Select an entity",
                           message: "See where it's mentioned, how it relates to others, and correct it.")
            }
        }
        .searchable(text: $model.search, placement: .toolbar, prompt: "Filter names")
        .onSubmit(of: .search) { model.refresh() }
        .onChange(of: model.search) { value in if value.isEmpty { model.refresh() } }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                FilterMenu(help: "Show one entity type",
                           options: [FilterOption(value: "", title: "All Types", systemImage: "square.grid.2x2")] +
                               KnowledgeVocabulary.entityTypes.map {
                                   FilterOption(value: $0, title: KnowledgeVocabulary.label($0).capitalized, systemImage: EntitySymbol.of($0))
                               },
                           selection: Binding(get: { model.typeFilter }, set: { model.setTypeFilter($0) }))
                Button { model.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            }
        }
        .onAppear { model.refresh() }
    }
}

enum EntitySymbol {
    static func of(_ type: String?) -> String {
        switch type {
        case "person": "person"
        case "organization": "building.2"
        case "project": "hammer"
        case "technology": "cpu"
        case "concept": "lightbulb"
        case "place": "mappin.and.ellipse"
        case "document": "doc.text"
        case "event": "calendar"
        default: "circle.dashed"
        }
    }
}

enum Count {
    static func label(_ count: Int, _ noun: String) -> String {
        "\(count) \(noun)\(count == 1 ? "" : "s")"
    }
}

enum TypeOrigin {
    static func label(_ origin: String) -> String {
        switch origin {
        case "owner": "set by you"
        case "verified": "verified"
        default: "local model"
        }
    }
}
