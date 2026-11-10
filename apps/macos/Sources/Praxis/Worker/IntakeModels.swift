import Foundation

struct IntakeFileError: Decodable, Identifiable {
    let sourcePath: String
    let message: String
    var id: String { sourcePath }
}

struct IntakeStatus: Decodable {
    let state: String
    let paused: Bool
    let lastSuccessfulScanUtc: String?
    let observedFiles: Int
    let importedSinceStart: Int
    let errorCount: Int
    let fileErrors: [IntakeFileError]
    let problem: String?
}

struct SourceMovement: Decodable, Identifiable {
    let sourceId: String
    let previousPath: String
    let currentPath: String
    let sha256: String
    let recordedUtc: String
    var id: String { "\(sourceId):\(recordedUtc):\(previousPath)" }
}
