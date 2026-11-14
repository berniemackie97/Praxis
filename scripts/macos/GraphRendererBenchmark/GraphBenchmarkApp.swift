import SwiftUI

@main
struct GraphBenchmarkApp: App {
    private let fixture: GraphFixture?

    init() {
        guard CommandLine.arguments.count == 2 else {
            fixture = nil
            return
        }
        fixture = try? GraphFixture.load(from: CommandLine.arguments[1])
    }

    var body: some Scene {
        WindowGroup("Graph renderer comparison") {
            if let fixture {
                BenchmarkScreen(fixture: fixture)
                    .frame(minWidth: 900, minHeight: 640)
            } else {
                Text("Provide one generated graph fixture JSON file as the command-line argument.")
                    .frame(minWidth: 700, minHeight: 300)
            }
        }
    }

    private struct BenchmarkScreen: View {
        let fixture: GraphFixture
        @Environment(\.accessibilityReduceMotion) private var reduceMotion
        @State private var useSpriteKit = false
        @State private var motion = true
        @State private var selection: String?

        var body: some View {
            VStack(spacing: 12) {
                HStack {
                    Picker("Renderer", selection: $useSpriteKit) {
                        Text("Canvas").tag(false)
                        Text("SpriteKit").tag(true)
                    }
                    .pickerStyle(.segmented)
                    .frame(width: 270)
                    Toggle("Animate", isOn: $motion)
                    Text("\(fixture.nodes.count) nodes · \(fixture.edges.count) edges")
                        .foregroundStyle(.secondary)
                    Spacer()
                }
                HStack(spacing: 0) {
                    Group {
                        if useSpriteKit {
                            SpriteGraphView(fixture: fixture, animated: motion && !reduceMotion,
                                            selection: $selection)
                        } else {
                            CanvasGraphView(fixture: fixture, animated: motion && !reduceMotion,
                                            selection: $selection)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .clipped()
                    List(fixture.nodes, selection: $selection) { node in
                        Text(node.label)
                            .accessibilityLabel("\(node.kind), \(node.label)")
                            .tag(node.id)
                    }
                    .frame(width: 190)
                }
                Text(reduceMotion ? "Reduce Motion: static graph" : "Synthetic positions; no source data or worker access")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(12)
        }
    }
}
