import SwiftUI

struct QuickNoteView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @FocusState private var bodyFocused: Bool

    private static let limit = 8_000
    private var canSave: Bool {
        !model.savingNote && !model.noteText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            model.noteText.count <= Self.limit && model.noteTitle.count <= 80
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: Spacing.m) {
                    TextField("Untitled note", text: $model.noteTitle)
                        .textFieldStyle(.plain)
                        .font(.title.weight(.semibold))
                        .disabled(model.savingNote)
                        .onSubmit { bodyFocused = true }
                    ZStack(alignment: .topLeading) {
                        if model.noteText.isEmpty {
                            Text("Write Markdown, paste a quotation or a link…")
                                .foregroundStyle(.tertiary)
                                .padding(.top, 1)
                                .allowsHitTesting(false)
                        }
                        TextEditor(text: $model.noteText)
                            .font(.system(.body))
                            .scrollContentBackground(.hidden)
                            .focused($bodyFocused)
                            .disabled(model.savingNote)
                            .frame(minHeight: 320)
                            .accessibilityLabel("Note content")
                    }
                }
                .padding(.horizontal, Spacing.xxl)
                .padding(.vertical, Spacing.xl)
                .frame(maxWidth: 760, alignment: .leading)
                .frame(maxWidth: .infinity)
            }
            Divider()
            HStack(spacing: Spacing.m) {
                Label("Saved as a private Markdown original in your Inbox. Nothing reads your clipboard or goes to a model.", systemImage: "lock")
                    .font(.meta).foregroundStyle(.secondary).lineLimit(2)
                Spacer()
                Text("\(model.noteText.count.formatted()) / \(Self.limit.formatted())")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(model.noteText.count > Self.limit ? Color.orange : Color.secondary)
                Button("Discard", role: .destructive) { model.discardNote() }
                    .disabled(model.savingNote || (model.noteText.isEmpty && model.noteTitle.isEmpty))
                Button(model.savingNote ? "Saving…" : "Save Note") { model.saveNote() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.return, modifiers: [.command])
                    .disabled(!canSave)
                    .help("Save (⌘↩)")
            }
            .padding(.horizontal, Spacing.l)
            .padding(.vertical, Spacing.m)
            .background(.bar)
        }
        .background(Color(nsColor: .textBackgroundColor))
        .onAppear { bodyFocused = true }
    }
}
