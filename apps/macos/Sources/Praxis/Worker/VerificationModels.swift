import Foundation

struct VerificationItem: Decodable, Identifiable {
    let ordinal: Int
    let proposalId: String?
    let entityId: String?
    let included: Bool
    let verdict: String?
    let correctedType: String?
    let note: String?
    var id: Int { ordinal }
}

struct VerificationRequest: Decodable, Identifiable {
    let requestId: String
    let reason: String
    let status: String
    let providerId: String?
    let createdUtc: String
    let failureCode: String?
    let modelName: String?
    let inputTokens: Int?
    let outputTokens: Int?
    let payload: String
    let items: [VerificationItem]
    var id: String { requestId }

    var reasonLabel: String {
        switch reason {
        case "instruction_like": "Evidence reads like an instruction to a model"
        case "type_conflict": "Mentions disagree about an entity's type"
        default: "Requested by you"
        }
    }

    var awaitingApproval: Bool { status == "awaiting_approval" }
}

/// One item of a verification payload, decoded for review. The payload JSON itself is what
/// the worker sends; this view never alters it.
struct PayloadItem: Identifiable {
    let ordinal: Int
    let kind: String
    let title: String
    let quotes: [(quote: String, context: String, source: String)]
    var id: Int { ordinal }

    static func parse(_ payload: String) -> [PayloadItem] {
        guard let data = payload.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let items = root["items"] as? [[String: Any]] else { return [] }
        return items.compactMap { item in
            guard let ordinal = item["ordinal"] as? Int, let kind = item["kind"] as? String else { return nil }
            let text = { (key: String) in item[key] as? String ?? "" }
            let title: String
            switch kind {
            case "relation": title = "\(text("subject")) \(KnowledgeVocabulary.label(text("predicate"))) \(text("object"))"
            case "entity_type": title = "Type of \(text("name"))"
            default: title = "\(text("name")) as \(text("statedType"))"
            }
            let contexts = (item["contexts"] as? [[String: Any]]) ?? [item]
            let quotes = contexts.map { context in
                (quote: context["quote"] as? String ?? "", context: context["context"] as? String ?? "",
                 source: context["source"] as? String ?? "")
            }
            return PayloadItem(ordinal: ordinal, kind: kind, title: title, quotes: quotes)
        }
    }
}

/// Mirrors the worker's narrowing rule (exclude items, then replace each phrase,
/// case-insensitively, in every text value) so the owner can preview what approval sends.
enum PayloadPreview {
    static let redacted = "[REDACTED]"

    static func narrow(_ payload: String, excluded: Set<Int>, redactions: [String]) -> String {
        guard let data = payload.data(using: .utf8),
              var root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let items = root["items"] as? [[String: Any]] else { return payload }
        root["items"] = items.filter { !excluded.contains($0["ordinal"] as? Int ?? -1) }
        let phrases = redactions.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let narrowed = redact(root, phrases)
        guard let output = try? JSONSerialization.data(
            withJSONObject: narrowed, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return payload }
        return String(decoding: output, as: UTF8.self)
    }

    /// Applies the redaction rule to one text value.
    static func redact(_ text: String, _ redactions: [String]) -> String {
        redactions.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            .reduce(text) { $0.replacingOccurrences(of: $1, with: redacted, options: .caseInsensitive) }
    }

    private static func redact(_ value: Any, _ phrases: [String]) -> Any {
        switch value {
        case let text as String:
            return redact(text, phrases)
        case let array as [Any]:
            return array.map { redact($0, phrases) }
        case let object as [String: Any]:
            return object.mapValues { redact($0, phrases) }
        default:
            return value
        }
    }
}

struct VerificationListData: Decodable { let requests: [VerificationRequest] }
struct VerificationData: Decodable { let request: VerificationRequest? }
struct VerificationCreatedData: Decodable { let verificationId: String? }
struct VerificationPlannedData: Decodable { let planned: Int }
struct VerificationDeclinedData: Decodable { let declined: Bool }

struct VerificationDisclosureSource: Decodable, Identifiable {
    let sourceId: String
    let name: String
    let classification: String
    let ordinals: [Int]
    var id: String { sourceId }
    var remoteAllowed: Bool { classification == "remote_verification" }
}

struct VerificationDisclosureEvent: Decodable, Identifiable {
    let eventId: String
    let requestId: String?
    let sourceId: String?
    let providerId: String?
    let actor: String
    let operation: String
    let result: String
    let reasonCode: String
    let occurredUtc: String
    var id: String { eventId }
}

struct VerificationDisclosureSourcesData: Decodable { let sources: [VerificationDisclosureSource] }
struct VerificationDisclosurePolicyData: Decodable { let changed: Bool }
struct VerificationDisclosureRevokedData: Decodable { let revoked: Bool }
struct VerificationDisclosureAuditData: Decodable { let events: [VerificationDisclosureEvent] }

enum VerificationProviderDefault {
    /// The Checking tier suggests an enabled provider. Local Ollama retains its existing empty fallback choice.
    static func choice(checkingProvider: String?, providers: [VerificationProvider]) -> String {
        guard let checkingProvider,
              providers.contains(where: { $0.providerId == checkingProvider && $0.enabled }) else { return "" }
        return checkingProvider == VerificationProvider.ollama ? "" : checkingProvider
    }

    static func available(_ selection: String, providers: [VerificationProvider]) -> Bool {
        let providerId = selection.isEmpty ? VerificationProvider.ollama : selection
        return providers.contains { $0.providerId == providerId && $0.enabled }
    }
}
