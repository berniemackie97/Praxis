import SwiftUI

struct SearchView: View {
    @ObservedObject var model: SearchWorkbenchModel

    private var submitted: String { model.submittedQuery }

    private var scopes: [FilterOption<String?>] {
        var options = [FilterOption<String?>(value: nil, title: "All Sources", systemImage: "books.vertical")]
        if let selected = model.selectedCollectionId, !model.collections.contains(where: { $0.collectionId == selected }) {
            options.append(FilterOption(value: selected, title: "Unavailable collection"))
        }
        return options + model.collections.map { FilterOption(value: $0.collectionId, title: $0.name, systemImage: "folder") }
    }

    var body: some View {
        VStack(spacing: 0) {
            if model.error != nil || model.notice != nil || !submitted.isEmpty {
                VStack(alignment: .leading, spacing: Spacing.s) {
                    HStack(spacing: Spacing.s) {
                        StatusPill(text: model.mode, tone: model.mode.hasPrefix("Hybrid") ? .accent : .neutral,
                                   systemImage: model.mode.hasPrefix("Hybrid") ? "sparkle.magnifyingglass" : "textformat")
                        if !submitted.isEmpty {
                            Text("\(Count.label(model.hits.count, "result")) for “\(submitted)”")
                                .font(.callout).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer()
                    }
                    if let notice = model.notice { Banner(text: notice, tone: .accent) }
                    if let error = model.error { Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill") }
                    if !model.videoClips.isEmpty { VideoMoments(clips: model.videoClips) }
                }
                .padding(.horizontal, Spacing.l)
                .padding(.vertical, Spacing.m)
                Divider()
            }
            if submitted.isEmpty {
                EmptyState(systemImage: "magnifyingglass", title: "Search everything you've captured",
                           message: "Type in the search field above. With a local embedding model enabled, results also match meaning, not just words.")
            } else {
                MasterDetail(listWidth: (280, 360, 520)) {
                    List(selection: Binding<String?>(
                        get: { model.hits.first(where: model.isPreviewing)?.id },
                        set: { id in if let hit = model.hits.first(where: { $0.id == id }) { model.select(hit) } })) {
                        ForEach(model.hits) { hit in
                            VStack(alignment: .leading, spacing: Spacing.xs) {
                                Label(hit.title, systemImage: SourceSymbol.of(hit.sourceLocator)).font(.rowTitle).lineLimit(1)
                                let snippet = SnippetText.clean(hit.snippet, locator: hit.sourceLocator)
                                if snippet.characters.isEmpty {
                                    Text("Matched the file name").font(.callout).foregroundStyle(.tertiary)
                                } else {
                                    Text(snippet).font(.callout).foregroundStyle(.secondary).lineLimit(3)
                                }
                            }
                            .padding(.vertical, Spacing.xs)
                            .tag(hit.id)
                        }
                    }
                    .overlay {
                        if model.hits.isEmpty {
                            EmptyState(systemImage: "text.magnifyingglass", title: "No matches",
                                       message: "Try different words, or search all sources instead of one collection.")
                        }
                    }
                } detail: {
                    if model.preview.isEmpty {
                        EmptyState(systemImage: "text.viewfinder", title: "Select a result", message: "Preview its extracted text here.")
                    } else {
                        ScrollView {
                            Text(model.preview)
                                .font(.system(.callout, design: .monospaced))
                                .lineSpacing(2)
                                .textSelection(.enabled)
                                .frame(maxWidth: 860, alignment: .leading)
                                .padding(Spacing.xl)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }
            }
        }
        .searchable(text: $model.query, placement: .toolbar, prompt: "Search sources")
        .onSubmit(of: .search) { model.submit() }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                FilterMenu(help: "Limit search to one collection", options: scopes, selection: $model.selectedCollectionId)
                .onChange(of: model.selectedCollectionId) { _ in if !submitted.isEmpty { model.submit() } }
                Menu {
                    Button("Update Semantic Index") { model.updateSemanticIndex() }
                    Button("Rebuild Semantic Index") { model.updateSemanticIndex(rebuild: true) }
                } label: {
                    if model.indexing {
                        Label("Indexing…", systemImage: "hourglass")
                    } else {
                        Label("Semantic Index", systemImage: "sparkle.magnifyingglass")
                    }
                }
                .disabled(model.indexing)
                .help("Embed current sources with your local embedding model")
            }
        }
        .onAppear { model.loadCollections() }
        .onDisappear { model.clearResults() }
    }
}

/// Search snippets as one readable run of text: the file path the index also stores is dropped,
/// whitespace is collapsed, and the index's ⟦match⟧ markers become bold.
enum SnippetText {
    static let open: Character = "⟦"
    static let close: Character = "⟧"

    static func plain(_ snippet: String, locator: String) -> String {
        var text = snippet
        // The path may carry match markers between its characters, so compare without them.
        let pattern = locator.map { NSRegularExpression.escapedPattern(for: String($0)) }.joined(separator: "[⟦⟧]*")
        if let range = text.range(of: pattern, options: .regularExpression) { text.removeSubrange(range) }
        return text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    static func clean(_ snippet: String, locator: String) -> AttributedString {
        var result = AttributedString()
        var bold = false
        var run = ""
        func flush() {
            guard !run.isEmpty else { return }
            var part = AttributedString(run)
            if bold {
                part.inlinePresentationIntent = .stronglyEmphasized
                part.foregroundColor = .primary
            }
            result += part
            run = ""
        }
        for character in plain(snippet, locator: locator) {
            if character == open || character == close {
                flush()
                bold = character == open
            } else {
                run.append(character)
            }
        }
        flush()
        return result
    }
}

private struct VideoMoments: View {
    let clips: [VideoCue]
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: Spacing.s) {
                ForEach(clips) { clip in
                    HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                        Text(clip.range).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 90, alignment: .leading)
                        VStack(alignment: .leading, spacing: Spacing.xxs) {
                            Text(clip.captionText).font(.callout).textSelection(.enabled)
                            Text(URL(fileURLWithPath: clip.videoLocator).lastPathComponent).font(.caption2).foregroundStyle(.tertiary)
                        }
                    }
                }
                Text("Timestamps come from preserved subtitles. Inline playback requires the separately authorized player workflow.")
                    .font(.caption2).foregroundStyle(.tertiary)
            }
            .padding(.top, Spacing.s)
        } label: {
            Label("\(Count.label(clips.count, "video moment")) from captions", systemImage: "film").font(.callout.weight(.medium))
        }
    }
}
