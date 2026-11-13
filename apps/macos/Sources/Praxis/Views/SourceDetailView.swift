import AppKit
import SwiftUI

struct SourceDetailView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var sourceToRevoke: LibraryItem?
    @State private var confirmRevoke = false

    var body: some View {
        if let item = model.selected {
            ScrollView {
                VStack(alignment: .leading, spacing: Spacing.l) {
                    header(item)
                    IntakePanel(understanding: model.understanding)
                    details(item)
                    collections(item)
                    ProposalReviewView()
                    if item.isVideo { media(item) }
                    if !model.sourceMoves.isEmpty { moves }
                    Panel(title: "Extracted text", systemImage: "text.alignleft") {
                        if model.libraryPreview.isEmpty {
                            Text("No extracted text yet.").font(.callout).foregroundStyle(.secondary)
                        } else {
                            Text(model.libraryPreview)
                                .font(.system(.callout, design: .monospaced))
                                .textSelection(.enabled)
                                .lineSpacing(2)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }
                .padding(Spacing.xl)
                .frame(maxWidth: 900, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .confirmationDialog("Revoke this source from active retrieval?",
                                isPresented: $confirmRevoke,
                                presenting: sourceToRevoke) { item in
                Button("Revoke Source", role: .destructive) { model.revoke(item) }
            } message: { item in
                Text("Remove \(item.title) from search and derived text. Its stored original is retained.")
            }
        } else {
            EmptyState(systemImage: "doc.text.magnifyingglass", title: "Select a source",
                       message: "See its original, extracted text, suggestions and collections.")
        }
    }

    private func header(_ item: LibraryItem) -> some View {
        ViewThatFits(in: .horizontal) {
            HStack(alignment: .top, spacing: Spacing.m) {
                identity(item).frame(minWidth: 260, alignment: .leading)
                Spacer(minLength: Spacing.l)
                actions(item)
            }
            VStack(alignment: .leading, spacing: Spacing.m) {
                identity(item)
                actions(item)
            }
        }
    }

    private func identity(_ item: LibraryItem) -> some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            Image(systemName: SourceSymbol.of(item.sourceLocator))
                .font(.system(size: 24, weight: .light))
                .foregroundStyle(Color.accentColor)
                .frame(width: 44, height: 44)
                .background(Color.accentColor.opacity(0.10), in: RoundedRectangle(cornerRadius: Radius.m, style: .continuous))
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(item.title).font(.pageTitle).textSelection(.enabled).lineLimit(2)
                Text(item.sourceLocator).font(.meta).foregroundStyle(.secondary).textSelection(.enabled)
                    .lineLimit(1).truncationMode(.middle)
                    .help(item.sourceLocator)
            }
        }
    }

    private func actions(_ item: LibraryItem) -> some View {
        HStack(spacing: Spacing.s) {
            Button(model.exportingOriginal ? "Exporting…" : "Export Original…") { chooseExportDestination(for: item) }
                .disabled(model.exportingOriginal || item.sourceStatus != "active")
            Menu {
                Button("Verify Original") { model.verify() }
                Button("Retry Extraction") { model.retry() }
                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.sourceLocator)]) }
                Divider()
                Button("Revoke…", role: .destructive) {
                    sourceToRevoke = item
                    confirmRevoke = true
                }
                .disabled(item.sourceStatus != "active")
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .accessibilityLabel("More actions")
        }
    }

    private func details(_ item: LibraryItem) -> some View {
        Panel(title: "Details", systemImage: "info.circle") {
            VStack(alignment: .leading, spacing: Spacing.s) {
                KeyValueRow(key: "Status", value: SourceState.detail(item))
                KeyValueRow(key: "Revision", value: "\(item.revision)")
                KeyValueRow(key: "Size", value: ByteCountFormatter.string(fromByteCount: item.originalBytes, countStyle: .file))
                KeyValueRow(key: "SHA-256", value: item.sha256, identifier: true)
                KeyValueRow(key: "Source ID", value: item.sourceId, identifier: true)
            }
        }
    }

    private func collections(_ item: LibraryItem) -> some View {
        let assigned = model.collections.filter { model.assignedCollectionIds.contains($0.collectionId) }
        let available = model.collections.filter { !model.assignedCollectionIds.contains($0.collectionId) }
        return Panel(title: "Collections", systemImage: "folder") {
            FlowLayout(spacing: Spacing.xs) {
                ForEach(assigned) { collection in
                    Chip(text: collection.name, systemImage: "folder") {
                        model.selectedMembershipId = collection.collectionId
                        model.removeMembership()
                    }
                }
                Menu {
                    ForEach(available) { collection in
                        Button(collection.name) {
                            model.selectedMembershipId = collection.collectionId
                            model.addMembership()
                        }
                    }
                } label: {
                    Label("Add", systemImage: "plus")
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .disabled(available.isEmpty || item.sourceStatus != "active")
            }
        }
    }

    private func media(_ item: LibraryItem) -> some View {
        Panel(title: "Media original", systemImage: "film",
              caption: "Header recognition is not a decoder, transcript verification or permission to play.") {
            VStack(alignment: .leading, spacing: Spacing.s) {
                if let inspected = model.mediaInspection,
                   inspected.sourceId == item.sourceId,
                   inspected.revision == item.revision,
                   inspected.sourceSha256 == item.sha256 {
                    KeyValueRow(key: "Signature", value: "\(inspected.outcome) · \(inspected.containerFamily ?? "unrecognized")")
                    KeyValueRow(key: "Duration", value: inspected.durationMilliseconds
                        .map { Duration.milliseconds($0).formatted(.time(pattern: .hourMinuteSecond)) } ?? "Unavailable from the bounded header")
                    KeyValueRow(key: "Verified", value: "\(ByteCountFormatter.string(fromByteCount: inspected.originalBytes, countStyle: .file)) · \(inspected.producer)")
                }
                Button(model.inspectingMedia ? "Inspecting…" : "Inspect Verified Header") { model.inspectSelectedMedia() }
                    .disabled(item.sourceStatus != "active" || model.inspectingMedia)
            }
        }
    }

    private var moves: some View {
        Panel(title: "Observed file moves", systemImage: "arrow.left.arrow.right") {
            VStack(alignment: .leading, spacing: Spacing.m) {
                ForEach(model.sourceMoves) { move in
                    VStack(alignment: .leading, spacing: Spacing.xxs) {
                        Text(move.currentPath).font(.callout).textSelection(.enabled)
                        Text("from \(move.previousPath)").font(.meta).foregroundStyle(.secondary).textSelection(.enabled)
                        Text(DateText.short(move.recordedUtc)).font(.caption2).foregroundStyle(.tertiary)
                    }
                }
            }
        }
    }

    private func chooseExportDestination(for item: LibraryItem) {
        let picker = NSSavePanel()
        picker.nameFieldStringValue = item.title
        picker.canCreateDirectories = true
        picker.prompt = "Export Original"
        picker.message = "Praxis verifies the preserved original before saving. Existing files will not be overwritten. Files saved to a synced folder may be uploaded by that service."
        guard picker.runModal() == .OK, let destination = picker.url else { return }
        model.exportOriginal(item, destination: destination)
    }
}
