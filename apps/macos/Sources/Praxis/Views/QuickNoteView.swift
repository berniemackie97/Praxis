import SwiftUI

struct QuickNoteView: View {
    @EnvironmentObject private var model: WorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Capture a thought").font(.title2.weight(.semibold))
            Text("Type or paste Markdown, a quotation, or a URL. Nothing reads your clipboard automatically, fetches websites, or sends content to a model.")
                .foregroundStyle(.secondary)
            TextField("Title (optional)", text: $model.noteTitle)
                .textFieldStyle(.roundedBorder)
                .disabled(model.savingNote)
            TextEditor(text: $model.noteText)
                .font(.system(.body, design: .monospaced))
                .disabled(model.savingNote)
                .border(Color.secondary.opacity(0.3))
                .accessibilityLabel("Note content")
            Text("Saving creates a private Markdown original in your approved Inbox. The optional title becomes its heading. Use Command-Return to save.")
                .font(.footnote).foregroundStyle(.secondary)
            HStack {
                Button(model.savingNote ? "Saving…" : "Save note") {
                    model.saveNote()
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.return, modifiers: [.command])
                .disabled(model.savingNote ||
                    model.noteText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                    model.noteText.count > 8_000 ||
                    model.noteTitle.count > 80)
                Button("Discard draft", role: .destructive) { model.discardNote() }
                    .disabled(model.savingNote ||
                        (model.noteText.isEmpty && model.noteTitle.isEmpty))
                Spacer()
                Text("\(model.noteText.count) / 8,000 characters")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}
