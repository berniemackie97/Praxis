import SwiftUI

/// Entities across all captured sources, with type, evidence counts and conflicts.
struct KnowledgeView: View {
    @ObservedObject var model: KnowledgeWorkbenchModel
    let verification: VerificationWorkbenchModel
    let collections: [SourceCollection]
    let sources: [LibraryItem]
    let explain: (String) -> Void
    let openSource: (GraphEvidence) -> Bool
    @AppStorage("knowledgeViewMode") private var viewMode = "list"

    var body: some View {
        VStack(spacing: 0) {
            Picker("Knowledge view", selection: $viewMode) {
                Label("Entities", systemImage: "list.bullet").tag("list")
                Label("Graph", systemImage: "point.3.connected.trianglepath.dotted").tag("graph")
            }
            .pickerStyle(.segmented)
            .frame(width: 230)
            .padding(.vertical, Spacing.s)
            .accessibilityLabel("Knowledge view")
            Divider()
            if viewMode == "graph" {
                KnowledgeGraphView(graph: model.graph, collections: collections, sources: sources,
                                   inspect: { entityId in
                                       model.selectEntityId(entityId)
                                       viewMode = "list"
                                   }, explain: explain, openSource: openSource)
            } else {
                listContent
            }
        }
    }

    private var listContent: some View {
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
                    EmptyState(systemImage: "point.3.connected.trianglepath.dotted", title: "No knowledge yet",
                               message: model.search.isEmpty && model.typeFilter.isEmpty
                                   ? "People, organizations, projects and ideas appear here once intake has understood and verified your files. Set up a Reading model in Settings → Models for the best results."
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
                Toggle(isOn: Binding(get: { model.includeSuggestions }, set: { model.setIncludeSuggestions($0) })) {
                    Label("Show Unconfirmed", systemImage: "questionmark.circle")
                }
                .toggleStyle(.button)
                .help("Also show names only the local model has guessed")
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
        case "class": "square.stack.3d.up"
        case "function": "function"
        case "binary_module": "shippingbox"
        case "data_asset": "doc.zipper"
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
        case "evidence": "source evidence"
        default: "local model"
        }
    }
}
