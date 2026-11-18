import SwiftUI

/// Effects come from the worker's receipt, not claims in generated prose.
struct CommandReceiptCard: View {
    let receipt: CommandReceipt
    let openApprovals: () -> Void
    let openPrivacy: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack {
                Label(receipt.title, systemImage: receipt.name == "dream" ? "moon.zzz" : "brain")
                    .font(.headline)
                Spacer()
                Text(receipt.outcomeLabel).font(.meta).foregroundStyle(.secondary)
            }
            Text(receipt.summary).font(.callout).textSelection(.enabled)
            if receipt.name == "dream" {
                Text("\(receipt.candidates) candidates · \(receipt.reviewed.count) source-backed entity reviews · \(receipt.reviewItems.count) review items · \(receipt.appliedChanges) corrections applied")
                    .font(.meta).foregroundStyle(.secondary)
            }
            if !receipt.reviewed.isEmpty {
                Text(receipt.reviewed.map(\.name).joined(separator: ", ")).font(.meta).foregroundStyle(.secondary)
            }
            if !receipt.reviewItems.isEmpty || receipt.outcome == "proposals_ready" {
                Button("Open Approvals", action: openApprovals).buttonStyle(.link)
            }
            if receipt.outcome == "blocked" {
                Button("Review Privacy permissions", action: openPrivacy).buttonStyle(.link)
            }
        }
        .padding(Spacing.m)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Surface.well, in: RoundedRectangle(cornerRadius: Radius.m))
        .overlay(RoundedRectangle(cornerRadius: Radius.m).strokeBorder(Surface.stroke))
        .accessibilityElement(children: .contain)
    }
}
