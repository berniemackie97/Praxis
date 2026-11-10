import Combine
import SwiftUI

struct PraxisWindow: View {
    @EnvironmentObject private var model: WorkbenchModel
    @AppStorage("dataRoot") private var dataRoot = ""
    @AppStorage("inbox") private var inbox = ""
    @AppStorage("configured") private var configured = false
    @AppStorage("dotnetExecutable") private var dotnetExecutable = ""
    @State private var section = Section.library
    private let refreshTimer = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading) {
                    Text("Praxis").font(.largeTitle.weight(.semibold))
                    Text("Local knowledge workbench").foregroundStyle(.secondary)
                }
                Spacer()
                Text(model.running
                    ? "Intake: \(model.intakeStatus?.state ?? "starting")"
                    : "Not running")
                    .foregroundColor(model.running && model.intakeStatus?.state == "watching"
                        ? Color.green : Color.secondary)
            }
            if model.running {
                Picker("Workspace", selection: $section) {
                    ForEach(Section.allCases, id: \.self) { section in
                        Text(section.rawValue).tag(section)
                    }
                }.pickerStyle(.segmented)
                workspace
            } else {
                SetupView(dataRoot: $dataRoot, inbox: $inbox, configured: $configured,
                          dotnetExecutable: $dotnetExecutable)
            }
            Divider()
            Text(model.message).font(.footnote).foregroundStyle(.secondary)
                .lineLimit(3).textSelection(.enabled)
        }
        .padding(20)
        .onAppear {
            if configured && !dataRoot.isEmpty && !inbox.isEmpty {
                model.start(dataRoot: dataRoot, inbox: inbox, initialize: false,
                            dotnetExecutable: dotnetExecutable)
            }
        }
        .onDisappear { model.stop() }
        .onReceive(refreshTimer) { _ in
            guard model.running else { return }
            model.refresh()
            if section == .verification { model.verification.refresh() }
        }
    }

    @ViewBuilder
    private var workspace: some View {
        switch section {
        case .library: LibraryView { section = .quickNote }
        case .quickNote: QuickNoteView()
        case .search: SearchView(model: model.search)
        case .knowledge: KnowledgeView(model: model.knowledge, verification: model.verification)
        case .verification: VerificationView(model: model.verification, providers: model.intelligence.providers)
        case .collections: CollectionsView()
        case .models: IntelligenceSettingsView(model: model.intelligence)
        case .configuration: ConfigurationView(dataRoot: dataRoot, inbox: inbox)
        }
    }

    private enum Section: String, CaseIterable {
        case library = "Inbox & Library"
        case quickNote = "Quick note"
        case search = "Search"
        case knowledge = "Knowledge"
        case verification = "Verification"
        case collections = "Collections"
        case models = "Models"
        case configuration = "Configuration"
    }
}
