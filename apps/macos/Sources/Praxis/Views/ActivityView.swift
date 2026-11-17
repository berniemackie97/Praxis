import SwiftUI

/// What Praxis is doing right now: each model call in flight with its file and elapsed time, every
/// stage's queue, each provider's calls against its daily limit, recent calls with their outcome
/// and duration, and plain notes on anything that blocks progress.
struct ActivityView: View {
    @ObservedObject var model: ActivityModel
    @ObservedObject var setup: ModelSetupModel
    let dream: (AgentBackendProfile) -> Void

    var body: some View {
        ScrollView {
            TimelineView(.periodic(from: .now, by: 1)) { context in
                VStack(alignment: .leading, spacing: Spacing.l) {
                    controls
                    if let brain = model.brain {
                        learning(brain)
                        processorControls(brain)
                    }
                    if let error = model.error {
                        Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill")
                    }
                    if let data = model.data {
                        ForEach(data.notices) { notice in
                            Banner(text: notice.text, tone: ActivityText.tone(notice.tone),
                                   systemImage: notice.tone == "info" ? "info.circle.fill" : "exclamationmark.triangle.fill")
                        }
                        now(data, at: context.date)
                        stages(data)
                        providers(data, at: context.date)
                        if !data.outcomes.isEmpty { outcomes(data) }
                        recent(data, at: context.date)
                    } else if model.error == nil {
                        HStack(spacing: Spacing.s) {
                            ProgressView().controlSize(.small)
                            Text("Loading activity…").foregroundStyle(.secondary)
                        }
                    }
                }
                .padding(Spacing.xl)
                .frame(maxWidth: 980, alignment: .leading)
            }
        }
        .task {
            setup.refresh()
            while !Task.isCancelled {
                model.refresh()
                try? await Task.sleep(for: .seconds(3))
            }
        }
    }

    // MARK: - Sections

    private var controls: some View {
        Panel(title: "Brain work", systemImage: "brain", caption: "Model changes affect future calls. Current work keeps its selected model. Privacy permissions still apply.") {
            VStack(alignment: .leading, spacing: Spacing.s) {
                if let error = setup.error {
                    Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill")
                }
                ForEach(["local", "strong", "strongest"], id: \.self) { role in
                    ModelRoleRow(setup: setup, role: role, installed: setup.machine?.installed ?? [])
                }
                Divider()
                HStack {
                    VStack(alignment: .leading, spacing: Spacing.xxs) {
                        Text("Dream").font(.rowTitle)
                        Text("Inspect evidence and propose knowledge repairs. Review before applying.")
                            .font(.meta).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Menu("Prepare Dream…") {
                        ForEach(setup.backends.filter(\.enabled)) { backend in
                            Button("\(ModelSetupModel.name(backend.backendId)) · \(backend.model)") { dream(backend) }
                        }
                    }
                    .disabled(!setup.backends.contains(where: \.enabled))
                    .help("Open a new Chat draft with this model. Add instructions after /dream, then send.")
                }
                Text("Remote triage can send permitted source excerpts to that provider. Local-only sources stay held by Privacy policy.")
                    .font(.meta).foregroundStyle(.secondary)
            }
        }
    }

    private func learning(_ brain: BrainWorkData) -> some View {
        Panel(title: "Learning from experience", systemImage: "lightbulb", caption: "Local-only review of useful tool receipts. Draft skills require approval in Skills; no model weights or permissions change.") {
            VStack(alignment: .leading, spacing: Spacing.s) {
                HStack {
                    Picker("Learning", selection: Binding(get: { brain.settings.mode }, set: { model.configureLearning(mode: $0) })) {
                        Text("Off").tag("off")
                        Text("Manual review").tag("manual")
                        Text("After useful work").tag("after_work")
                        Text("Idle maintenance").tag("idle")
                    }
                    Spacer()
                    Button("Review Next") { model.reviewLearning() }.disabled(brain.settings.mode == "off")
                }
                HStack {
                    Picker("Local review model", selection: Binding(get: { brain.settings.model ?? "" }, set: { model.configureLearning(model: $0) })) {
                        Text("Shared local model").tag("")
                        ForEach((setup.machine?.installed ?? []).filter(\.supportsChat)) { Text($0.name).tag($0.name) }
                    }
                    Spacer()
                    Stepper("Up to \(brain.settings.dailyCalls) reviews/day", value: Binding(get: { brain.settings.dailyCalls }, set: { model.configureLearning(dailyCalls: $0) }), in: 1...100)
                }
                Picker("Daily token budget", selection: Binding(get: { brain.settings.dailyTokens }, set: { model.configureLearning(dailyTokens: $0) })) {
                    ForEach(Array(Set([6_000, 12_000, 24_000, 48_000, brain.settings.dailyTokens])).sorted(), id: \.self) { Text($0.formatted()).tag($0) }
                }
                Toggle("Dream during idle time when knowledge changes", isOn: Binding(get: { brain.settings.idleDream }, set: { model.configureLearning(idleDream: $0) }))
                    .disabled(brain.settings.mode == "off" || brain.settings.mode == "manual")
                Text("Requires Propose edits or Allow edits. Each attempt reserves at least 6,000 tokens; unused daily capacity is not a usage charge. Actual reported usage appears below.")
                    .font(.meta).foregroundStyle(.secondary)
                ForEach(brain.learning.prefix(8)) { receipt in
                    HStack {
                        Text(receipt.state.replacingOccurrences(of: "_", with: " ").capitalized)
                        Spacer()
                        Text("\(receipt.inputTokens.formatted()) in · \(receipt.outputTokens.formatted()) out")
                            .foregroundStyle(.secondary)
                    }.font(.meta)
                }
            }.disabled(model.changingBrain)
        }
    }

    private func processorControls(_ brain: BrainWorkData) -> some View {
        Panel(title: "Processor controls", systemImage: "pause.circle", caption: "Pause stops new leases and lets active work drain. State survives restart; resume keeps existing results and queued work.") {
            VStack(spacing: Spacing.s) {
                ForEach(brain.processors) { processor in
                    HStack {
                        VStack(alignment: .leading, spacing: Spacing.xxs) {
                            Text(ActivityText.stage(processor.processorKey)).font(.rowTitle)
                            Text("\(processor.state.replacingOccurrences(of: "_", with: " ")) · \(processor.running) running · \(processor.waiting) waiting")
                                .font(.meta).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button(processor.paused ? "Resume" : "Pause") { model.setPaused(processor) }
                            .disabled(model.changingBrain)
                    }
                }
            }
        }
    }

    private func now(_ data: ActivityData, at date: Date) -> some View {
        Panel(title: "Working now", systemImage: "waveform.path.ecg",
              caption: data.calling.isEmpty && data.running.isEmpty ? nil : "Updates every few seconds.") {
            if data.calling.isEmpty && data.running.isEmpty {
                Text(idleText(data)).font(.callout).foregroundStyle(.secondary)
            } else {
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    ForEach(data.calling) { call in
                        ItemRow(systemImage: "cpu", title: call.fileName ?? purpose(call.purpose),
                                subtitle: "\(ActivityText.role(call.tier)) · \(ActivityText.provider(call.providerId))" +
                                    (call.model.map { " · \($0)" } ?? ""),
                                tint: .accentColor) {
                            elapsed(call.startedUtc, at: date)
                        }
                    }
                    let busy = Set(data.calling.compactMap(\.fileName))
                    ForEach(data.running.filter { !busy.contains($0.fileName) || $0.stage != "intake-understand-v1" }) { job in
                        ItemRow(systemImage: "gearshape.2", title: job.fileName,
                                subtitle: ActivityText.stage(job.stage) + (job.attempt > 1 ? " · attempt \(job.attempt)" : "")) {
                            if stalled(job, at: date) {
                                StatusPill(text: "Stopped reporting", tone: .warning)
                            } else {
                                elapsed(job.startedUtc, at: date)
                            }
                        }
                    }
                }
            }
        }
    }

    private func stages(_ data: ActivityData) -> some View {
        Panel(title: "Pipeline", systemImage: "arrow.triangle.branch", caption: "Every file goes through these stages in order.") {
            Grid(alignment: .leading, horizontalSpacing: Spacing.l, verticalSpacing: Spacing.s) {
                GridRow {
                    Text("Stage")
                    Text("Waiting").gridColumnAlignment(.trailing)
                    Text("Running").gridColumnAlignment(.trailing)
                    Text("Failed").gridColumnAlignment(.trailing)
                    Text("Done today").gridColumnAlignment(.trailing)
                }
                .font(.meta).foregroundStyle(.secondary)
                Divider()
                ForEach(ordered(data.stages)) { stage in
                    GridRow {
                        Text(ActivityText.stage(stage.stage)).font(.callout)
                        count(stage.queued, tone: .neutral)
                        count(stage.running, tone: .accent)
                        count(stage.failed, tone: .critical)
                        count(stage.doneToday, tone: .positive)
                    }
                }
            }
        }
    }

    private func providers(_ data: ActivityData, at date: Date) -> some View {
        Panel(title: "Models", systemImage: "cpu", caption: paceCaption(data)) {
            if data.providers.isEmpty {
                Text("No model is connected. Connect one in Settings › Models.").font(.callout).foregroundStyle(.secondary)
            } else {
                VStack(alignment: .leading, spacing: Spacing.m) {
                    ForEach(data.providers) { provider in providerRow(provider, at: date) }
                }
            }
        }
    }

    private func providerRow(_ provider: ProviderActivity, at date: Date) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                Text(ActivityText.provider(provider.providerId)).font(.rowTitle)
                if !provider.roles.isEmpty {
                    Text(provider.roles.map(ActivityText.role).joined(separator: ", ")).font(.meta).foregroundStyle(.secondary)
                }
                Spacer(minLength: Spacing.s)
                if !provider.enabled {
                    StatusPill(text: "Off", tone: .neutral)
                } else if provider.dailyLimit > 0, provider.callsToday >= provider.dailyLimit {
                    StatusPill(text: "Daily limit reached", tone: .warning)
                } else if let outcome = provider.lastOutcome {
                    StatusPill(text: "Last call: " + ActivityText.outcome(outcome), tone: outcome == "completed" ? .positive : .critical)
                }
            }
            if provider.dailyLimit > 0 {
                ProgressView(value: Double(min(provider.callsToday, provider.dailyLimit)), total: Double(provider.dailyLimit))
                    .tint(provider.callsToday >= provider.dailyLimit ? .orange : .accentColor)
            }
            Text(usage(provider, at: date)).font(.meta).foregroundStyle(.secondary)
        }
    }

    private func outcomes(_ data: ActivityData) -> some View {
        Panel(title: "Intake results", systemImage: "tray.full", caption: "Where every current file ended up.") {
            FlowLayout(spacing: Spacing.xs) {
                ForEach(data.outcomes) { outcome in
                    Chip(text: "\(ActivityText.intake(outcome)): \(outcome.count.formatted())",
                         systemImage: outcome.status == "understood" ? "checkmark.seal" : "doc.text.magnifyingglass")
                }
            }
        }
    }

    private func recent(_ data: ActivityData, at date: Date) -> some View {
        Panel(title: "Recent model calls", systemImage: "clock.arrow.circlepath") {
            if data.recentCalls.isEmpty {
                Text("No model calls yet.").font(.callout).foregroundStyle(.secondary)
            } else {
                Grid(alignment: .leading, horizontalSpacing: Spacing.m, verticalSpacing: Spacing.s) {
                    GridRow {
                        Text("When")
                        Text("Role")
                        Text("Model")
                        Text("File")
                        Text("Took").gridColumnAlignment(.trailing)
                        Text("Result")
                    }
                    .font(.meta).foregroundStyle(.secondary)
                    Divider()
                    ForEach(data.recentCalls) { call in
                        GridRow {
                            Text(DateText.relative(call.startedUtc, now: date)).foregroundStyle(.secondary)
                            Text(ActivityText.role(call.tier))
                            Text(ActivityText.provider(call.providerId) + (call.model.map { " · \($0)" } ?? "")).lineLimit(1)
                            Text(call.fileName ?? purpose(call.purpose)).lineLimit(1).truncationMode(.middle)
                                .help(call.fileName ?? "")
                            Text(ActivityText.duration(call.durationMs)).monospacedDigit()
                            StatusPill(text: ActivityText.outcome(call.outcome), tone: call.outcome == "completed" ? .positive : .critical)
                        }
                        .font(.callout)
                    }
                }
            }
        }
    }

    // MARK: - Pieces

    private func elapsed(_ iso: String, at date: Date) -> some View {
        Text(ActivityText.elapsed(since: iso, now: date)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
    }

    private func count(_ value: Int, tone: Tone) -> some View {
        Text(value == 0 ? "–" : value.formatted())
            .font(.callout.monospacedDigit())
            .foregroundStyle(value == 0 ? Color.secondary : (tone == .neutral ? Color.primary : tone.color))
    }

    private func stalled(_ job: RunningJob, at date: Date) -> Bool {
        guard let lease = job.leaseUntilUtc, let until = DateText.date(lease) else { return false }
        return until < date
    }

    private func idleText(_ data: ActivityData) -> String {
        let waiting = data.stages.reduce(0) { $0 + $1.queued }
        return waiting == 0 ? "Nothing to do. New files start here as soon as they arrive."
            : "\(waiting.formatted()) job(s) waiting. If nothing starts, check the notes above."
    }

    private func usage(_ provider: ProviderActivity, at date: Date) -> String {
        var parts = [provider.dailyLimit > 0 ? "\(provider.callsToday) of \(provider.dailyLimit) calls today"
            : "\(provider.callsToday) calls today, no limit"]
        if provider.failuresLastHour > 0 { parts.append("\(provider.failuresLastHour) failed in the last hour") }
        if let last = provider.lastCallUtc { parts.append("last call \(DateText.relative(last, now: date))") }
        return parts.joined(separator: " · ")
    }

    private func paceCaption(_ data: ActivityData) -> String? {
        guard let pace = data.localSecondsPerThousandCharacters else { return nil }
        return String(format: "Local model pace on this Mac: %.1f s per 1,000 characters.", pace)
    }

    private func purpose(_ code: String) -> String {
        switch code {
        case "intake": "Intake"
        case "chat": "Chat"
        case "verification": "Verification"
        case "indexing": "Indexing Brain"
        case "learning": "Reviewing task experience"
        case "knowledge-maintenance": "Reviewing Brain knowledge"
        default: code.capitalized
        }
    }

    private func ordered(_ stages: [StageCount]) -> [StageCount] {
        let order = ["source-text-v1", "text-segments-v1", "markdown-heading-v1", "intake-triage-v1", "intake-understand-v1", "semantic-extract-v2"]
        return stages.sorted { (order.firstIndex(of: $0.stage) ?? order.count, $0.stage) < (order.firstIndex(of: $1.stage) ?? order.count, $1.stage) }
    }
}
