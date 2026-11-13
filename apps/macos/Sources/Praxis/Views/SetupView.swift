import AppKit
import SwiftUI

struct SetupView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Binding var dataRoot: String
    @Binding var inbox: String
    @Binding var additionalInboxes: String
    @Binding var rootApprovals: String
    @Binding var configured: Bool
    @Binding var dotnetExecutable: String

    private var extraFolders: [String] {
        (try? WatchedFolderSettings.folders(additionalInboxes)) ?? []
    }

    var body: some View {
        ScrollView {
            VStack(spacing: Spacing.xl) {
                VStack(spacing: Spacing.s) {
                    Image(systemName: "brain.head.profile")
                        .font(.system(size: 44, weight: .light))
                        .foregroundStyle(Color.accentColor)
                    Text("Welcome to Praxis").font(.largeTitle.weight(.semibold))
                    Text("Your private second brain. Choose where Praxis keeps its catalog and which folders it watches. Everything stays on this Mac unless you allow otherwise.")
                        .font(.callout).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 460)
                }
                VStack(spacing: 0) {
                    SetupStep(number: 1, title: "Catalog location",
                              detail: "Praxis creates a private PraxisData folder here. Avoid cloud-synced folders if you don't want them uploaded.",
                              value: dataRoot.isEmpty ? nil : dataRoot) {
                        Button(dataRoot.isEmpty ? "Choose…" : "Change…") {
                            if let selected = chooseFolder() {
                                dataRoot = selected.appendingPathComponent("PraxisData").path
                                configured = false
                            }
                        }
                    }
                    Divider().padding(.leading, 52)
                    SetupStep(number: 2, title: "Primary Inbox",
                              detail: "Files you drop here are captured. Quick notes are saved here too.",
                              value: inbox.isEmpty ? nil : inbox) {
                        Button(inbox.isEmpty ? "Choose…" : "Change…") {
                            if let selected = chooseFolder() {
                                let canonical = CanonicalPath.of(selected.path)
                                inbox = canonical.path
                                rememberApproval(canonical)
                                configured = false
                            }
                        }
                    }
                    Divider().padding(.leading, 52)
                    SetupStep(number: 3, title: "More watched folders",
                              detail: "Optional, up to three. They are read in place, never copied or moved.",
                              value: nil) {
                        Button("Add…") {
                            if let selected = chooseFolder() {
                                let canonical = CanonicalPath.of(selected.path)
                                var folders = extraFolders
                                folders.append(canonical.path)
                                additionalInboxes = WatchedFolderSettings.encode(folders)
                                rememberApproval(canonical)
                                configured = false
                            }
                        }
                        .disabled(extraFolders.count >= 3)
                    }
                    if !extraFolders.isEmpty {
                        VStack(alignment: .leading, spacing: Spacing.xs) {
                            ForEach(extraFolders, id: \.self) { path in
                                HStack(spacing: Spacing.s) {
                                    Image(systemName: "folder").foregroundStyle(.secondary)
                                    Text(path).font(.meta).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                                    Spacer()
                                    Button {
                                        additionalInboxes = WatchedFolderSettings.encode(extraFolders.filter { $0 != path })
                                        configured = false
                                    } label: { Image(systemName: "minus.circle") }
                                        .buttonStyle(.borderless)
                                        .accessibilityLabel("Stop watching \(path)")
                                }
                            }
                        }
                        .padding(.leading, 52)
                        .padding([.trailing, .bottom], Spacing.l)
                    }
                }
                .background(Surface.card, in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: Radius.l, style: .continuous).strokeBorder(Surface.stroke.opacity(0.6)))

                DisclosureGroup("Advanced") {
                    HStack {
                        Text(dotnetExecutable.isEmpty ? "Using standard .NET locations" : dotnetExecutable)
                            .font(.meta).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button("Locate .NET…") {
                            let picker = NSOpenPanel()
                            picker.canChooseFiles = true
                            picker.canChooseDirectories = false
                            picker.allowsMultipleSelection = false
                            if picker.runModal() == .OK, let selected = picker.url {
                                dotnetExecutable = selected.path
                            }
                        }
                        if !dotnetExecutable.isEmpty {
                            Button("Reset") { dotnetExecutable = "" }
                        }
                    }
                    .padding(.top, Spacing.s)
                }
                .font(.callout)

                if !model.message.isEmpty && model.message != "Select a private catalog and watched Inbox." {
                    Banner(text: model.message, tone: .warning, systemImage: "exclamationmark.triangle.fill")
                }

                Button {
                    model.start(dataRoot: dataRoot, inbox: inbox,
                                additionalInboxes: additionalInboxes, rootApprovals: rootApprovals,
                                initialize: !configured, dotnetExecutable: dotnetExecutable)
                    if model.running { configured = true }
                } label: {
                    Text("Start Praxis").frame(minWidth: 160)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .keyboardShortcut(.defaultAction)
                .disabled(dataRoot.isEmpty || inbox.isEmpty)
            }
            .frame(maxWidth: 560)
            .padding(Spacing.xxl)
            .frame(maxWidth: .infinity)
        }
    }

    private func rememberApproval(_ folder: URL) {
        guard let signature = WatchedFolderSettings.identity(folder) else { return }
        var approved = (try? WatchedFolderSettings.approvals(rootApprovals)) ?? [:]
        approved[folder.path] = signature
        rootApprovals = WatchedFolderSettings.encode(approved)
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

private struct SetupStep<Action: View>: View {
    let number: Int
    let title: String
    let detail: String
    let value: String?
    @ViewBuilder var action: Action

    var body: some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            ZStack {
                Circle().fill(value == nil ? Surface.well : Color.accentColor.opacity(0.15)).frame(width: 28, height: 28)
                if value == nil {
                    Text("\(number)").font(.callout.weight(.semibold)).foregroundStyle(.secondary)
                } else {
                    Image(systemName: "checkmark").font(.caption.weight(.bold)).foregroundStyle(Color.accentColor)
                }
            }
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title).font(.rowTitle)
                Text(detail).font(.meta).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if let value {
                    Text(value).font(.meta).foregroundStyle(.primary).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                        .padding(.top, Spacing.xxs)
                }
            }
            Spacer(minLength: Spacing.m)
            action
        }
        .padding(Spacing.l)
    }
}
