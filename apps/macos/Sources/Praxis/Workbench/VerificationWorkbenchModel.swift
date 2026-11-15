import Combine
import Foundation

/// Verification requests: review the exact payload, exclude items, redact phrases, approve or
/// decline, and read verdicts.
@MainActor
final class VerificationWorkbenchModel: ObservableObject {
    private let channel: WorkerChannel

    @Published private(set) var requests: [VerificationRequest] = []
    @Published private(set) var selectedId: String?
    @Published private(set) var selected: VerificationRequest?
    @Published private(set) var providers: [VerificationProvider] = []
    @Published private(set) var loadingDetail = false
    @Published private(set) var deciding = false
    @Published var excluded: Set<Int> = [] { didSet { if excluded != oldValue { remoteConsent = false } } }
    @Published var redactions: [String] = [] { didSet { if redactions != oldValue { remoteConsent = false } } }
    @Published private(set) var providerChoice = VerificationProvider.ollama { didSet { if providerChoice != oldValue { remoteConsent = false } } }
    @Published var remoteConsent = false
    private var loadingList = false
    @Published private(set) var disclosureSources: [VerificationDisclosureSource] = []
    @Published private(set) var disclosureAudit: [VerificationDisclosureEvent] = []
    /// The latest failure or confirmation; setting one clears the other.
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    var pendingIDs: [String] { requests.filter(\.awaitingApproval).map(\.requestId) }
    var awaitingCount: Int { pendingIDs.count }
    var canApproveLocalBatch: Bool {
        !deciding && !pendingIDs.isEmpty && excluded.isEmpty && redactions.isEmpty &&
            providers.contains { $0.providerId == VerificationProvider.ollama }
    }

    /// Exactly what approval sends with the current exclusions and redactions.
    var preview: String {
        guard let selected else { return "" }
        return selected.awaitingApproval
            ? PayloadPreview.narrow(selected.payload, excluded: excluded, redactions: redactions)
            : PayloadPreview.narrow(selected.payload, excluded: [], redactions: [])
    }

    func refresh(includeReview: Bool = false) {
        if !loadingList {
            loadingList = true
            channel.send("verifications", ["limit": 100, "summariesOnly": true], as: VerificationListData.self,
                         failed: { [weak self] message in
                self?.loadingList = false
                self?.report(message)
            }) { [weak self] data in
                guard let self else { return }
                loadingList = false
                requests = data.requests
                if let selectedId, !deciding, !loadingDetail {
                    let status = requests.first { $0.id == selectedId }
                    if includeReview || status?.status != selected?.status || status?.failureCode != selected?.failureCode {
                        loadDetail(selectedId)
                    }
                }
            }
        }
        if includeReview {
            channel.send("verificationProviders", as: ProvidersData.self, failed: report) { [weak self] data in
                guard let self else { return }
                let oldRevision = providers.first { $0.providerId == providerChoice }?.revision
                providers = data.providers.filter(\.enabled)
                if oldRevision != providers.first(where: { $0.providerId == providerChoice })?.revision {
                    remoteConsent = false
                }
            }
            refreshAuthorization()
        }
    }

    func select(_ request: VerificationRequest) {
        guard selectedId != request.requestId else { return }
        selectedId = request.requestId
        selected = request.payload.isEmpty ? nil : request
        error = nil
        notice = nil
        excluded = []
        redactions = []
        providerChoice = VerificationProvider.ollama
        remoteConsent = false
        disclosureSources = []
        loadDetail(request.requestId)
        refreshAuthorization()
    }

    private func loadDetail(_ id: String) {
        channel.reset("verification-detail")
        loadingDetail = true
        channel.send("verification", ["verificationId": id], as: VerificationData.self,
                     scope: "verification-detail", failed: { [weak self] message in
            self?.loadingDetail = false
            self?.report(message)
        }) { [weak self] data in
            self?.selected = data.request
            self?.loadingDetail = false
        }
    }

    func chooseProvider(_ providerId: String) {
        providerChoice = providerId
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
        guard let selected, selected.awaitingApproval, !deciding, !loadingDetail else { return }
        deciding = true
        var fields: [String: Any] = [
            "verificationId": selected.requestId,
            "excludedOrdinals": excluded.sorted(),
            "redactions": redactions
        ]
        fields["providerId"] = providerChoice
        if let provider = providers.first(where: { $0.providerId == providerChoice }) {
            fields["providerRevision"] = provider.revision
        }
        fields["remoteDisclosureApproved"] = remoteConsent
        channel.send("verificationApprove", fields, as: VerificationData.self, failed: decisionFailed) { [weak self] data in
            guard let self else { return }
            if selectedId == data.request?.id { self.selected = data.request }
            deciding = false
            let provider = data.request?.providerId.map(VerificationProvider.displayName) ?? "a verifier"
            notice = "Queued for \(provider)."
            refresh(includeReview: true)
        }
    }

    func approveLocalBatch(_ ids: [String]) {
        guard canApproveLocalBatch, !ids.isEmpty else { return }
        deciding = true
        channel.send("verificationApproveLocalBatch", ["verificationIds": ids], as: VerificationBatchApprovalData.self,
                     failed: decisionFailed) { [weak self] data in
            guard let self else { return }
            deciding = false
            let skipped = data.skipped.isEmpty ? "" : " \(data.skipped.count) skipped. Review those requests individually."
            notice = "Queued \(Count.label(data.queued.count, "request")) for local Ollama." + skipped
            refresh(includeReview: true)
        }
    }

    private func decisionFailed(_ message: String) {
        deciding = false
        report(message)
        refresh(includeReview: true)
    }

    func setRemoteVerification(_ sourceId: String, allowed: Bool) {
        channel.send("verificationSourceDisclosureSet",
                     ["sourceId": sourceId, "allowRemoteVerification": allowed],
                     as: VerificationDisclosurePolicyData.self, failed: report) { [weak self] data in
            self?.notice = data.changed ? "Source disclosure classification updated." : "Source classification unchanged."
            self?.refresh(includeReview: true)
        }
    }

    func revokeDisclosure() {
        guard let selected else { return }
        channel.send("verificationDisclosureRevoke", ["verificationId": selected.requestId],
                     as: VerificationDisclosureRevokedData.self, failed: report) { [weak self] data in
            self?.notice = data.revoked ? "External verification grant revoked." : "No active grant to revoke."
            self?.refresh(includeReview: true)
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
        guard let selected, !deciding else { return }
        deciding = true
        channel.send("verificationDecline", ["verificationId": selected.requestId],
                     as: VerificationDeclinedData.self, failed: decisionFailed) { [weak self] result in
            self?.deciding = false
            self?.notice = result.declined ? "Declined. These items will not be suggested again." : "Request already changed."
            self?.refresh(includeReview: true)
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
