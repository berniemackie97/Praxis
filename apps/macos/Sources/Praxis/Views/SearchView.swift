import SwiftUI

struct SearchView: View {
    @ObservedObject var model: SearchWorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                TextField("Search extracted text", text: $model.query)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { model.submit() }
                Button("Search") { model.submit() }
                Button(model.indexing ? "Indexing…" : "Update semantic index") {
                    model.updateSemanticIndex()
                }
                .disabled(model.indexing)
                Button("Rebuild") { model.updateSemanticIndex(rebuild: true) }
                    .disabled(model.indexing)
                    .help("Rebuild the derived semantic index from current permitted sources")
            }
            HStack {
                Picker("Search in", selection: $model.selectedCollectionId) {
                    Text("All sources").tag(nil as String?)
                    ForEach(model.collections) { collection in
                        Text(collection.name).tag(Optional(collection.collectionId))
                    }
                }
                .pickerStyle(.menu)
                .onChange(of: model.selectedCollectionId) { _, _ in model.submit() }
                Button("Refresh collections") { model.loadCollections() }
            }
            Text(model.mode).font(.caption).foregroundStyle(.secondary)
            if model.selectedCollectionId != nil {
                Text("Video caption cues are omitted from collection-filtered search.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let notice = model.notice {
                Text(notice).font(.caption).foregroundStyle(.secondary)
            }
            if let error = model.error {
                Text(error).font(.callout).foregroundStyle(.secondary)
            }
            if !model.videoClips.isEmpty {
                DisclosureGroup("Source-linked video caption moments (\(model.videoClips.count))") {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(model.videoClips) { clip in
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("\(URL(fileURLWithPath: clip.videoLocator).lastPathComponent) · \(clip.range)")
                                        .fontWeight(.semibold)
                                    Text(clip.captionText).textSelection(.enabled)
                                    Text("Video \(clip.videoSourceId) · Revision \(clip.videoRevision) · Captions \(clip.captionSourceId)")
                                        .font(.caption2).foregroundStyle(.secondary)
                                        .textSelection(.enabled)
                                }
                            }
                        }.frame(maxWidth: .infinity, alignment: .leading)
                    }.frame(maxHeight: 180)
                    Text("These timestamps are grounded in preserved subtitles. Inline playback requires the separately authorized player workflow.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            HStack(alignment: .top) {
                List(model.hits) { hit in
                    Button { model.select(hit) } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(hit.title).fontWeight(.medium)
                            Text(hit.sourceLocator).font(.caption).foregroundStyle(.secondary)
                            Text(hit.snippet).font(.callout).lineLimit(3)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .listRowBackground(model.isPreviewing(hit) ? Color.accentColor.opacity(0.15) : Color.clear)
                }.frame(minWidth: 350)
                Divider()
                ScrollView {
                    if model.preview.isEmpty {
                        Text("Select a search result to preview its extracted text.")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    } else {
                        Text(model.preview)
                            .font(.system(.body, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .textSelection(.enabled)
                    }
                }
            }
        }
        .onAppear { model.loadCollections() }
    }
}
