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
    @Published private(set) var reviewingLearning = false
    @Published private(set) var learningFeedback: String?

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
        guard !reviewingLearning else { return }
        reviewingLearning = true
        learningFeedback = nil
        channel.send("brainLearningReview", as: LearningReviewRequest.self, failed: { [weak self] message in
            self?.reviewingLearning = false
            self?.error = message
        }) { [weak self] result in
            self?.reviewingLearning = false
            self?.error = nil
            self?.learningFeedback = switch result.status {
            case "queued": "Review queued. It will run when Chat and local model work are idle."
            case "review_running": "A learning review is already running."
            case "no_pending_work": "No eligible completed Chat runs are waiting for review."
            case "daily_call_limit": "Today's learning review limit has been reached."
            case "daily_token_limit": "The daily learning token budget has been reached."
            case "permission_required": "Enable Propose edits or Allow edits under Privacy to review experience."
            case "local_model_required": "Configure an available local review model."
            case "off": "Enable learning before requesting a review."
            default: "Review request could not be confirmed."
            }
            self?.refresh()
        }
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

private struct LearningReviewRequest: Decodable {
    let status: String
}

