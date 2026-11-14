import Combine
import Foundation

/// The Activity page: what each model is doing, what runs and waits, and why. Refreshed while visible.
@MainActor
final class ActivityModel: ObservableObject {
    private let channel: WorkerChannel

    @Published private(set) var data: ActivityData?
    @Published private(set) var error: String?
    @Published private(set) var updated: Date?

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    func refresh() {
        channel.send("activity", ["recent": 40], as: ActivityData.self, scope: "activity", failed: { [weak self] message in
            self?.error = message
        }) { [weak self] data in
            self?.data = data
            self?.error = nil
            self?.updated = Date()
        }
    }
}
