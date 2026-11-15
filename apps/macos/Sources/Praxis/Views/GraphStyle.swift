import Foundation

enum GraphStyle: String, CaseIterable, Identifiable {
    case neuralNebula
    case cellularAtlas

    var id: String { rawValue }

    var title: String {
        switch self {
        case .neuralNebula: "Knowledge Cosmos"
        case .cellularAtlas: "Cellular Atlas"
        }
    }
}
