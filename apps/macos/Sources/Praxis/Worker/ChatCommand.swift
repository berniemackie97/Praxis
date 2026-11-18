import Foundation

/// Metadata comes from the worker. Selecting a command never changes permissions or starts work.
struct ChatCommand: Decodable, Equatable, Identifiable {
    let name: String
    let title: String
    let description: String
    let argumentHint: String
    let minimumTier: String
    let requiresTools: Bool
    let localOnly: Bool
    var id: String { name }

    static func parse(_ text: String) -> (name: String, arguments: String)? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.first == "/" else { return nil }
        let parts = trimmed.dropFirst().split(maxSplits: 1, whereSeparator: \.isWhitespace)
        guard let word = parts.first, !word.isEmpty, word.count <= 32,
              word.first?.isASCII == true, word.first?.isLetter == true,
              word.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else { return nil }
        return (word.lowercased(), parts.count > 1 ? String(parts[1]).trimmingCharacters(in: .whitespacesAndNewlines) : "")
    }
}
