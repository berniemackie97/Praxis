import SwiftUI

/// How one source went through intake: how it was read, what triage decided, which tier understood
/// it, how much was verified, what it yielded, and why anything is still pending.
struct IntakePanel: View {
    let understanding: SourceUnderstanding?

    var body: some View {
        Panel(title: "Intake", systemImage: "sparkles") {
            if let understanding {
                StatusPill(text: UnderstandingText.status(understanding.status), tone: tone(understanding.status))
            }
        } content: {
            if let understanding {
                VStack(alignment: .leading, spacing: Spacing.m) {
                    if let summary = understanding.summary ?? understanding.gist {
                        Text(summary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    }
                    FlowLayout(spacing: Spacing.xs) {
                        Chip(text: read(understanding), systemImage: "doc.viewfinder")
                        if let category = understanding.category {
                            Chip(text: [category.capitalized, understanding.documentType].compactMap { $0 }.joined(separator: " · "),
                                 systemImage: "tag")
                        }
                        if let tier = UnderstandingText.tier(understanding.tier), understanding.tier != "hint" {
                            Chip(text: "Understood by \(tier)" + (understanding.providerId.map { " (\($0))" } ?? ""), systemImage: "cpu")
                        }
                        if understanding.verifiedItems + understanding.rejectedItems > 0 {
                            Chip(text: "\(understanding.verifiedItems) verified" +
                                 (understanding.rejectedItems > 0 ? ", \(understanding.rejectedItems) dropped" : ""), systemImage: "checkmark.seal")
                        }
                        if understanding.records > 0 {
                            Chip(text: Count.label(understanding.records, "record"), systemImage: "calendar")
                        }
                        if understanding.transactions > 0 {
                            Chip(text: Count.label(understanding.transactions, "transaction"), systemImage: "banknote")
                        }
                    }
                    if let reason = UnderstandingText.reason(understanding.reason) {
                        Banner(text: reason, tone: understanding.status == "held" ? .warning : .accent,
                               systemImage: understanding.status == "held" ? "hand.raised.fill" : "info.circle.fill")
                    }
                    if understanding.inputTokens + understanding.outputTokens > 0 {
                        Text("\((understanding.inputTokens + understanding.outputTokens).formatted()) model tokens")
                            .font(.caption2).foregroundStyle(.tertiary)
                    }
                }
            } else {
                HStack(spacing: Spacing.s) {
                    ProgressView().controlSize(.small)
                    Text("Waiting to be read.").font(.callout).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func read(_ understanding: SourceUnderstanding) -> String {
        var text = "Read as \(UnderstandingText.format(understanding.format))"
        if let pages = understanding.pages { text += ", \(Count.label(pages, "page"))" }
        if understanding.truncated { text += " (truncated)" }
        return text
    }

    private func tone(_ status: String) -> Tone {
        switch status {
        case "understood": .positive
        case "partially_verified": .accent
        case "held": .warning
        case "failed": .critical
        default: .neutral
        }
    }
}
