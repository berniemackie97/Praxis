import Combine
import Foundation

/// Verification requests: review the exact payload, exclude items, redact phrases, approve or
/// decline, and read verdicts.
@MainActor
final class VerificationWorkbenchModel: ObservableObject {
    private let channel: WorkerChannel

    @Published private(set) var requests: [VerificationRequest] = []
    @Published private(set) var selectedId: String?
    @Published var excluded: Set<Int> = []
    @Published var redactions: [String] = []
    @Published var providerChoice = "" { didSet { if providerChoice != oldValue { remoteConsent = false } } }
    @Published var remoteConsent = false
    @Published private(set) var disclosureSources: [VerificationDisclosureSource] = []
    @Published private(set) var disclosureAudit: [VerificationDisclosureEvent] = []
    /// The latest failure or confirmation; setting one clears the other.
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    var selected: VerificationRequest? { requests.first { $0.requestId == selectedId } }
    var awaitingCount: Int { requests.filter(\.awaitingApproval).count }

    /// Exactly what approval sends with the current exclusions and redactions.
    var preview: String {
        guard let selected else { return "" }
        return selected.awaitingApproval
            ? PayloadPreview.narrow(selected.payload, excluded: excluded, redactions: redactions)
            : PayloadPreview.narrow(selected.payload, excluded: [], redactions: [])
    }

    func refresh() {
        channel.send("verifications", ["limit": 100], as: VerificationListData.self, failed: report) { [weak self] data in
            self?.requests = data.requests
            self?.refreshAuthorization()
        }
    }

    func select(_ request: VerificationRequest) {
        guard selectedId != request.requestId else { return }
        selectedId = request.requestId
        excluded = []
        redactions = []
        providerChoice = ""
        remoteConsent = false
        disclosureSources = []
        refreshAuthorization()
        notice = nil
    }

    func toggle(_ ordinal: Int) {
        if excluded.contains(ordinal) { excluded.remove(ordinal) } else { excluded.insert(ordinal) }
    }

    func addRedaction(_ phrase: String) {
        let trimmed = phrase.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...200).contains(trimmed.count), !redactions.contains(trimmed), redactions.count < 50 else { return }
        redactions.append(trimmed)
    }

    func removeRedaction(_ phrase: String) {
        redactions.removeAll { $0 == phrase }
    }

    func plan() {
        channel.send("verificationPlan", as: VerificationPlannedData.self, failed: report) { [weak self] data in
            self?.notice = data.planned == 0 ? "Nothing new needs checking." : "\(data.planned) new request(s) to review."
            self?.refresh()
        }
    }

    func approve() {
        guard let selected, selected.awaitingApproval else { return }
        var fields: [String: Any] = [
            "verificationId": selected.requestId,
            "excludedOrdinals": excluded.sorted(),
            "redactions": redactions
        ]
        if !providerChoice.isEmpty { fields["providerId"] = providerChoice }
        fields["remoteDisclosureApproved"] = remoteConsent
        channel.send("verificationApprove", fields, as: VerificationData.self, failed: report) { [weak self] data in
            let provider = data.request?.providerId.map(VerificationProvider.displayName) ?? "a verifier"
            self?.notice = "Approved for \(provider)."
            self?.refresh()
        }
    }

    func setRemoteVerification(_ sourceId: String, allowed: Bool) {
        channel.send("verificationSourceDisclosureSet",
                     ["sourceId": sourceId, "allowRemoteVerification": allowed],
                     as: VerificationDisclosurePolicyData.self, failed: report) { [weak self] data in
            self?.notice = data.changed ? "Source disclosure classification updated." : "Source classification unchanged."
            self?.refreshAuthorization()
        }
    }

    func revokeDisclosure() {
        guard let selected else { return }
        channel.send("verificationDisclosureRevoke", ["verificationId": selected.requestId],
                     as: VerificationDisclosureRevokedData.self, failed: report) { [weak self] data in
            self?.notice = data.revoked ? "External verification grant revoked." : "No active grant to revoke."
            self?.refresh()
        }
    }

    private func refreshAuthorization() {
        if let selectedId {
            channel.send("verificationDisclosureSources", ["verificationId": selectedId],
                         as: VerificationDisclosureSourcesData.self, failed: report) { [weak self] data in
                guard self?.selectedId == selectedId else { return }
                self?.disclosureSources = data.sources
            }
        }
        channel.send("verificationDisclosureAudit", ["limit": 30],
                     as: VerificationDisclosureAuditData.self, failed: report) { [weak self] data in
            self?.disclosureAudit = data.events
        }
    }

    func decline() {
        guard let selected else { return }
        channel.send("verificationDecline", ["verificationId": selected.requestId],
                     as: VerificationDeclinedData.self, failed: report) { [weak self] _ in
            self?.notice = "Declined. These items will not be suggested again."
            self?.refresh()
        }
    }

    func requestForSource(_ sourceId: String) {
        channel.send("verificationRequestSource", ["sourceId": sourceId],
                     as: VerificationCreatedData.self, failed: report) { [weak self] data in
            self?.created(data.verificationId)
        }
    }

    func requestForEntity(_ entityId: String) {
        channel.send("verificationRequestEntity", ["entityId": entityId],
                     as: VerificationCreatedData.self, failed: report) { [weak self] data in
            self?.created(data.verificationId)
        }
    }

    private func created(_ id: String?) {
        notice = id == nil
            ? "Nothing to check: there are no pending entities or relations with visible evidence."
            : "Request created. Review it under Verification before anything is sent."
        refresh()
    }

    private func report(_ message: String) {
        error = message
    }
}
