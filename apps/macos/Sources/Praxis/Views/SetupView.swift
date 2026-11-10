import AppKit
import SwiftUI

struct SetupView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Binding var dataRoot: String
    @Binding var inbox: String
    @Binding var configured: Bool
    @Binding var dotnetExecutable: String

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Set up your private Second Brain").font(.title2)
            Text("Choose a parent for a private PraxisData folder and a separate existing Inbox. Use folders outside cloud-sync services if you do not want OS-level synchronization.")
                .foregroundStyle(.secondary)
            HStack {
                Button("Choose catalog location…") {
                    if let selected = chooseFolder() {
                        dataRoot = selected.appendingPathComponent("PraxisData").path
                        configured = false
                    }
                }
                Text(dataRoot.isEmpty ? "Not selected" : dataRoot)
                    .textSelection(.enabled).lineLimit(2)
            }
            HStack {
                Button("Choose watched Inbox…") {
                    if let selected = chooseFolder() {
                        inbox = selected.path
                        configured = false
                    }
                }
                Text(inbox.isEmpty ? "Not selected" : inbox)
                    .textSelection(.enabled).lineLimit(2)
            }
            HStack {
                Button("Locate .NET executable (optional)…") {
                    let picker = NSOpenPanel()
                    picker.canChooseFiles = true
                    picker.canChooseDirectories = false
                    picker.allowsMultipleSelection = false
                    if picker.runModal() == .OK, let selected = picker.url {
                        dotnetExecutable = selected.path
                    }
                }
                Text(dotnetExecutable.isEmpty ? "Using standard .NET locations" : dotnetExecutable)
                    .font(.caption).textSelection(.enabled).lineLimit(2)
            }
            Button("Start Praxis") {
                model.start(dataRoot: dataRoot, inbox: inbox, initialize: !configured,
                            dotnetExecutable: dotnetExecutable)
                if model.running { configured = true }
            }
            .buttonStyle(.borderedProminent)
            .disabled(dataRoot.isEmpty || inbox.isEmpty)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private func chooseFolder() -> URL? {
        let picker = NSOpenPanel()
        picker.canChooseDirectories = true
        picker.canChooseFiles = false
        picker.allowsMultipleSelection = false
        picker.canCreateDirectories = true
        return picker.runModal() == .OK ? picker.url : nil
    }
}
