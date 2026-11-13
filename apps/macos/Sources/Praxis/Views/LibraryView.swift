import SwiftUI

struct LibraryView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let openQuickNote: () -> Void

    var body: some View {
        MasterDetail(listWidth: (260, 320, 460)) {
            List(selection: Binding<String?>(
                get: { model.selected?.id },
                set: { id in if let item = model.items.first(where: { $0.id == id }) { model.select(item) } })) {
                ForEach(model.items) { item in
                    ItemRow(systemImage: SourceSymbol.of(item.sourceLocator), title: item.title,
                            subtitle: SourceState.subtitle(item)) {
                        if let state = SourceState.badge(item) {
                            StatusPill(text: state.text, tone: state.tone)
                        }
                    }
                    .tag(item.id)
                }
            }
            .overlay {
                if model.items.isEmpty {
                    EmptyState(systemImage: "tray",
                               title: model.selectedCollectionId.isEmpty ? "Nothing captured yet" : "This collection is empty",
                               message: model.selectedCollectionId.isEmpty
                                   ? "Drop files into a watched folder or write a quick note. Praxis keeps the original and extracts the text."
                                   : "Add sources to it from a source's detail pane.",
                               actionTitle: model.selectedCollectionId.isEmpty ? "New Quick Note" : nil,
                               action: model.selectedCollectionId.isEmpty ? openQuickNote : nil)
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) { Pager() }
        } detail: {
            SourceDetailView()
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                FilterMenu(help: "Show one collection",
                           options: [FilterOption(value: "", title: "All Sources", systemImage: "books.vertical")] +
                               model.collections.map { FilterOption(value: $0.collectionId, title: $0.name, systemImage: "folder") },
                           selection: Binding(get: { model.selectedCollectionId }, set: { model.selectCollectionFilter($0) }))
                Button(action: openQuickNote) { Label("New Quick Note", systemImage: "square.and.pencil") }
                    .help("New quick note (⇧⌘N)")
                Button { model.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            }
        }
    }
}

private struct Pager: View {
    @EnvironmentObject private var model: WorkbenchModel

    var body: some View {
        if model.page > 0 || model.items.count >= 100 {
            VStack(spacing: 0) {
                Divider()
                HStack {
                    Button { model.page -= 1; model.refresh() } label: { Image(systemName: "chevron.left") }
                        .disabled(model.page == 0)
                        .accessibilityLabel("Previous page")
                    Spacer()
                    Text("Page \(model.page + 1)").font(.meta).foregroundStyle(.secondary)
                    Spacer()
                    Button { model.page += 1; model.refresh() } label: { Image(systemName: "chevron.right") }
                        .disabled(model.items.count < 100)
                        .accessibilityLabel("Next page")
                }
                .buttonStyle(.borderless)
                .padding(.horizontal, Spacing.m)
                .padding(.vertical, Spacing.s)
            }
            .background(.bar)
        }
    }
}

/// How a source's processing state reads in lists and details.
enum SourceState {
    static func detail(_ item: LibraryItem) -> String {
        let extraction: String = switch item.extractionOutcome {
        case "completed": "text extracted"
        case "unsupported": "no text reader for this format yet"
        case nil: "not extracted yet"
        case let other?: other.replacingOccurrences(of: "_", with: " ")
        }
        let processing = item.processingState.map { $0 == "completed" ? nil : $0 } ?? "queued"
        return ([item.sourceStatus.capitalized, processing, extraction].compactMap { $0 }).joined(separator: " · ")
    }

    static func subtitle(_ item: LibraryItem) -> String {
        var parts = [ByteCountFormatter.string(fromByteCount: item.originalBytes, countStyle: .file)]
        if item.revision > 1 { parts.append("revision \(item.revision)") }
        if let outcome = item.extractionOutcome, outcome != "completed" { parts.append(outcome.replacingOccurrences(of: "_", with: " ")) }
        return parts.joined(separator: " · ")
    }

    static func badge(_ item: LibraryItem) -> (text: String, tone: Tone)? {
        if item.sourceStatus != "active" { return (item.sourceStatus.capitalized, .neutral) }
        switch item.processingState {
        case "failed": return ("Failed", .critical)
        case "queued", nil: return ("Queued", .neutral)
        case "running": return ("Processing", .accent)
        default: return nil
        }
    }
}
