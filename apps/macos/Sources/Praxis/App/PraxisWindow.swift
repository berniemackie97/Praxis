import Combine
import SwiftUI

struct PraxisWindow: View {
    @EnvironmentObject private var model: WorkbenchModel
    @AppStorage("dataRoot") private var dataRoot = ""
    @AppStorage("inbox") private var inbox = ""
    @AppStorage("additionalInboxes") private var additionalInboxes = "[]"
    @AppStorage("rootApprovals") private var rootApprovals = "{}"
    @AppStorage("configured") private var configured = false
    @AppStorage("dotnetExecutable") private var dotnetExecutable = ""
    @SceneStorage("workspace") private var section = AppSection.chat
    @State private var ticks = 0
    private let refreshTimer = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    var body: some View {
        Group {
            if model.running {
                WorkbenchSplitView(section: $section)
            } else {
                SetupView(dataRoot: $dataRoot, inbox: $inbox,
                          additionalInboxes: $additionalInboxes, rootApprovals: $rootApprovals,
                          configured: $configured, dotnetExecutable: $dotnetExecutable)
                    .navigationTitle("Praxis")
            }
        }
        .focusedSceneValue(\.workspace, $section)
        .modifier(SaveLinkPresenter(links: model.links))
        .onAppear {
            // The Xcode app-hosted test target must never auto-open an owner's saved catalog.
            guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil else {
                return
            }
            if configured && !dataRoot.isEmpty && !inbox.isEmpty {
                model.start(dataRoot: dataRoot, inbox: inbox,
                            additionalInboxes: additionalInboxes, rootApprovals: rootApprovals,
                            initialize: false, dotnetExecutable: dotnetExecutable)
            }
        }
        .onDisappear { model.stop() }
        .onReceive(refreshTimer) { _ in
            guard model.running, !model.workerBacklogged else { return }
            ticks += 1
            model.refresh()
            model.approvals.refresh()
            if section == .knowledge && ticks % 2 == 0 { model.knowledge.graph.recheckInspection() }
            if section == .verification || ticks % 6 == 0 { model.verification.refresh() }
        }
    }
}

/// The running workbench: sidebar navigation and the selected workspace.
struct WorkbenchSplitView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Binding var section: AppSection

    var body: some View {
        NavigationSplitView {
            Sidebar(selection: $section, approvals: model.approvals, verification: model.verification)
                .navigationSplitViewColumnWidth(min: 200, ideal: 224, max: 300)
        } detail: {
            workspace
                .navigationTitle(section.title)
                .navigationSubtitle(section.subtitle)
        }
    }

    @ViewBuilder
    private var workspace: some View {
        switch section {
        case .chat: ChatView(model: model.chat) { SettingsOpener.open(.models) }
        case .approvals: ApprovalsView(model: model.approvals)
        case .library: LibraryView { section = .quickNote }
        case .quickNote: QuickNoteView()
        case .search: SearchView(model: model.search)
        case .knowledge: KnowledgeView(model: model.knowledge, verification: model.verification,
                                        collections: model.collections, sources: model.items,
                                        explain: { prompt in
                                            if model.chat.prepareDraft(prompt) { section = .chat }
                                        }, openSource: { evidence in
                                            guard let current = model.items.first(where: evidence.matchesCurrent) else {
                                                return false
                                            }
                                            model.select(current)
                                            section = .library
                                            return true
                                        })
        case .verification: VerificationView(model: model.verification, providers: model.intelligence.providers)
        case .activity: ActivityView(model: model.activity)
        case .collections: CollectionsView { collectionId in
            model.selectCollectionFilter(collectionId)
            section = .library
        }
        }
    }
}

/// Grouped workspace navigation with live badges and the intake status at the bottom.
private struct Sidebar: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Binding var selection: AppSection
    @ObservedObject var approvals: ApprovalsWorkbenchModel
    @ObservedObject var verification: VerificationWorkbenchModel

    var body: some View {
        List(selection: Binding<AppSection?>(get: { selection }, set: { if let next = $0 { selection = next } })) {
            ForEach(AppSection.groups) { group in
                Section(group.title) {
                    ForEach(group.sections) { section in
                        Label(section.title, systemImage: section.systemImage)
                            .badge(badge(for: section))
                            .tag(section)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom, spacing: 0) {
            IntakeFooter { selection = .activity }
        }
    }

    private func badge(for section: AppSection) -> Int {
        switch section {
        case .approvals: approvals.count
        case .verification: verification.awaitingCount
        default: 0
        }
    }
}

/// Intake health in one glance; opens Activity for what each model is doing, the gear opens folder settings.
private struct IntakeFooter: View {
    @EnvironmentObject private var model: WorkbenchModel
    let showActivity: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            Divider()
            HStack(alignment: .top, spacing: Spacing.s) {
                Button(action: showActivity) {
                    VStack(alignment: .leading, spacing: Spacing.xs) {
                        StatusPill(text: IntakeHealth.label(model), tone: IntakeHealth.tone(model))
                        if let backlog = model.intakeBacklog, backlog.queued + backlog.running + backlog.failed + (backlog.enrichmentPending ?? 0) > 0 {
                            Text(IntakeHealth.backlog(backlog)).font(.meta).foregroundStyle(.secondary)
                        }
                        if !model.message.isEmpty {
                            Text(model.message)
                                .font(.meta).foregroundStyle(.secondary)
                                .lineLimit(2)
                                .help(model.message)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Show what each model is doing")
                .accessibilityLabel("Intake status: \(IntakeHealth.label(model)). Opens Activity.")
                Button { SettingsOpener.open(.general) } label: {
                    Image(systemName: "gearshape").foregroundStyle(.tertiary)
                }
                .buttonStyle(.plain)
                .help("Folder settings")
                .accessibilityLabel("Folder settings")
            }
            .padding(.horizontal, Spacing.m)
            .padding(.bottom, Spacing.m)
        }
    }
}

/// How intake health is summarized for the owner.
enum IntakeHealth {
    @MainActor
    static func label(_ model: WorkbenchModel) -> String {
        guard let status = model.intakeStatus else { return "Starting" }
        if needsAttention(model) { return "Needs attention" }
        if status.paused { return "Paused" }
        return status.state == "watching" ? "Watching folders" : status.state.replacingOccurrences(of: "_", with: " ").capitalized
    }

    @MainActor
    static func tone(_ model: WorkbenchModel) -> Tone {
        guard let status = model.intakeStatus else { return .neutral }
        if needsAttention(model) { return .warning }
        if status.paused { return .neutral }
        return status.state == "watching" ? .positive : .accent
    }

    @MainActor
    static func needsAttention(_ model: WorkbenchModel) -> Bool {
        model.intakeStatus?.problem != nil || model.intakeRoots.contains { $0.status.state == "reapproval_required" || $0.status.problem != nil }
    }

    static func backlog(_ backlog: ProcessingBacklog) -> String {
        var parts: [String] = []
        if backlog.running > 0 { parts.append("\(backlog.running) processing") }
        if backlog.queued > 0 { parts.append("\(backlog.queued) queued") }
        if backlog.failed > 0 { parts.append("\(backlog.failed) failed") }
        if parts.isEmpty { parts.append("Intake up to date") }
        if let enrichment = backlog.enrichmentPending, enrichment > 0 { parts.append("local enrichment: \(enrichment) left") }
        return parts.joined(separator: " · ")
    }
}
