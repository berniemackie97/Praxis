import SwiftUI

/// Living Cortex's zoom-tier ladder and truth-versus-ornament legend.
struct LivingCortexHUD: View {
    let tier: Int
    let fit: () -> Void
    let up: () -> Void

    private static let tiers = ["Whole brain", "Territory", "Neural network", "Neuron + evidence"]

    var body: some View {
        HStack(alignment: .bottom) {
            VStack(alignment: .leading, spacing: Spacing.s) {
                Text("ZOOM TIER").font(.caption2.monospaced()).foregroundStyle(.secondary)
                HStack(spacing: Spacing.xs) {
                    ForEach(Array(Self.tiers.enumerated()), id: \.offset) { index, title in
                        VStack(alignment: .leading, spacing: 2) {
                            Text("T\(index)").font(.caption2.monospaced()).opacity(0.75)
                            Text(title).font(.caption.weight(.semibold))
                        }
                        .padding(.horizontal, Spacing.s).padding(.vertical, Spacing.xs)
                        .frame(minWidth: 88, alignment: .leading)
                        .background(index == tier ? Color(red: 1, green: 0.42, blue: 0.32).opacity(0.35) : Color.black.opacity(0.45),
                                    in: RoundedRectangle(cornerRadius: Radius.s))
                        .overlay(RoundedRectangle(cornerRadius: Radius.s)
                            .strokeBorder(index == tier ? Color(red: 1, green: 0.6, blue: 0.48) : Color.white.opacity(0.1)))
                        .foregroundStyle(index == tier ? Color.white : Color.white.opacity(0.7))
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel("Tier \(index), \(title)\(index == tier ? ", current" : "")")
                    }
                }
                HStack(spacing: Spacing.s) {
                    Button("Fit", action: fit)
                    Button("Up one tier", action: up).disabled(tier == 0)
                }
                .controlSize(.small)
            }
            Spacer()
            VStack(alignment: .leading, spacing: Spacing.xs) {
                legend(Capsule().fill(Color(red: 1, green: 0.69, blue: 0.6)).frame(width: 22, height: 2),
                       "evidence-backed pathway or synapse (pulse = direction)")
                legend(Capsule().strokeBorder(Color.white.opacity(0.4), style: StrokeStyle(lineWidth: 1, dash: [2, 2])).frame(width: 22, height: 3),
                       "ornamental: folds, smoke, dendrites")
                legend(Circle().strokeBorder(Color.gray, style: StrokeStyle(lineWidth: 1, dash: [2, 2])).frame(width: 10, height: 10),
                       "forming region (queued, not knowledge)")
            }
            .font(.caption2)
            .foregroundStyle(Color.white.opacity(0.85))
            .padding(Spacing.s)
            .background(Color.black.opacity(0.45), in: RoundedRectangle(cornerRadius: Radius.m))
            .accessibilityElement(children: .combine)
        }
        .padding(Spacing.m)
    }

    private func legend(_ swatch: some View, _ text: String) -> some View {
        HStack(spacing: Spacing.s) {
            swatch.frame(width: 24, alignment: .center)
            Text(text)
        }
    }
}
