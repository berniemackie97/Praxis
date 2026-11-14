import Foundation

/// How intake read, triaged and understood a source's current revision (ADR 0044).
struct SourceUnderstanding: Decodable, Equatable {
    let itemId: String
    let revision: Int
    let format: String
    let pages: Int?
    let title: String?
    let truncated: Bool
    let category: String?
    let documentType: String?
    let language: String?
    let gist: String?
    let summary: String?
    let status: String
    let reason: String?
    let tier: String?
    let providerId: String?
    let model: String?
    let verifiedItems: Int
    let rejectedItems: Int
    let records: Int
    let transactions: Int
    let inputTokens: Int
    let outputTokens: Int
    /// How the tiers handed the document along, e.g. "local drafted 12 → strong kept 9, removed 3, added 4 → 13 verified".
    let cascade: String?
}

struct SourceUnderstandingData: Decodable { let understanding: SourceUnderstanding? }

/// Owner-facing wording for intake states.
enum UnderstandingText {
    static func status(_ status: String) -> String {
        switch status {
        case "read": "Read, waiting for triage"
        case "triaged": "Triaged, waiting to be understood"
        case "understood": "Understood"
        case "partially_verified": "Partly verified"
        case "held": "Waiting for your approval"
        case "index_only": "Searchable only"
        case "skipped": "Skipped by your policy"
        case "failed": "Could not be verified"
        default: status
        }
    }

    static func reason(_ reason: String?) -> String? {
        switch reason {
        case nil: nil
        case "awaiting_disclosure_approval": "A stronger model needs your approval in Approvals."
        case "index_only": "Your policy kept this local, so it is searchable but not deeply understood."
        case "skipped_by_policy": "Your policy skips this category."
        case "verification_failed": "No model answer passed verification against the document."
        case "partially_verified": "Only the parts that matched the document were kept."
        case "document_longer_than_understood": "The document is longer than one pass covers; the rest is searchable."
        case "tier_unavailable", "not_configured": "No models are set up yet. Add them in Settings → Models and Agents to have intake understand this."
        case "statement_too_long": "The statement is too long to transcribe in one pass."
        case let other?: other.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }

    static func format(_ format: String) -> String {
        switch format {
        case "docx": "Word document"
        case "xlsx": "Spreadsheet"
        case "pdf": "PDF"
        case "email": "Email"
        case "html": "Web page"
        case "image": "Image (OCR)"
        case "delimited": "Table"
        case "financial_interchange": "Bank export"
        case "markdown": "Markdown"
        case "subtitles": "Subtitles"
        case "code": "Code"
        default: "Text"
        }
    }

    static func tier(_ tier: String?) -> String? {
        switch tier {
        case "local": "local model"
        case "strong": "strong model"
        case "strongest": "strongest model"
        case "reused": "an identical file"
        case "hint": "its format"
        default: nil
        }
    }
}
