import Foundation

enum GraphStyle: String, CaseIterable, Identifiable {
    case livingCortex
    case neuralNebula
    case cellularAtlas

    var id: String { rawValue }

    var title: String {
        switch self {
        case .livingCortex: "Living Cortex"
        case .neuralNebula: "Knowledge Cosmos"
        case .cellularAtlas: "Cellular Atlas"
        }
    }
}
