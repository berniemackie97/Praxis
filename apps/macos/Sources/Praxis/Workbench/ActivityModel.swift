import Combine
import Foundation

/// The Activity page: what each model is doing, what runs and waits, and why. Refreshed while visible.
@MainActor
final class ActivityModel: ObservableObject {
    private let channel: WorkerChannel

    @Published private(set) var data: ActivityData?
    @Published private(set) var error: String?
    @Published private(set) var updated: Date?
    @Published private(set) var brain: BrainWorkData?
    @Published private(set) var changingBrain = false

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    func refresh() {
        channel.send("brainWork", as: BrainWorkData.self, scope: "brainWork", failed: { [weak self] in self?.error = $0 }) { [weak self] in self?.brain = $0 }
        channel.send("activity", ["recent": 40], as: ActivityData.self, scope: "activity", failed: { [weak self] message in
            self?.error = message
        }) { [weak self] data in
            self?.data = data
            self?.error = nil
            self?.updated = Date()
        }
    }

    func setPaused(_ processor: ProcessorControl) {
        change("processorPause", ["processorKey": processor.processorKey, "paused": !processor.paused])
    }

    func configureLearning(mode: String? = nil, model: String? = nil, idleDream: Bool? = nil,
                           dailyCalls: Int? = nil, dailyTokens: Int? = nil) {
        guard let current = brain?.settings else { return }
        var fields: [String: Any] = ["mode": mode ?? current.mode,
            "dailyCalls": dailyCalls ?? current.dailyCalls, "dailyTokens": dailyTokens ?? current.dailyTokens,
            "idleDream": idleDream ?? current.idleDream, "dreamIntervalMinutes": current.dreamIntervalMinutes]
        let chosen = model ?? current.model ?? ""
        if !chosen.isEmpty { fields["model"] = chosen }
        change("brainLearningSet", fields)
    }

    func reviewLearning() {
        channel.send("brainLearningReview", as: IgnoredData.self, failed: { [weak self] in self?.error = $0 }) { [weak self] _ in self?.refresh() }
    }

    private func change(_ action: String, _ fields: [String: Any]) {
        guard !changingBrain else { return }
        changingBrain = true
        channel.send(action, fields, as: BrainWorkData.self, failed: { [weak self] message in
            self?.changingBrain = false
            self?.error = message
        }) { [weak self] result in
            self?.changingBrain = false
            self?.brain = result
            self?.error = nil
        }
    }

}
