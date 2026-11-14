import Foundation

struct VideoCue: Decodable, Identifiable {
    let captionSourceId: String
    let captionItemId: String
    let captionRevision: Int
    let captionSha256: String
    let captionOffset: Int
    let captionLength: Int
    let cueNumber: Int
    let startMilliseconds: Int64
    let endMilliseconds: Int64
    let captionText: String
    let producer: String
    let videoSourceId: String
    let videoItemId: String
    let videoRevision: Int
    let videoSha256: String
    let videoLocator: String
    var id: String { "\(captionItemId):\(captionRevision):\(cueNumber)" }
    var range: String {
        "\(startMilliseconds / 1000)s–\(endMilliseconds / 1000)s"
    }
}

struct MediaInspection: Decodable {
    let sourceId: String
    let itemId: String
    let revision: Int
    let sourceSha256: String
    let originalBytes: Int64
    let sourceExtension: String
    let outcome: String
    let containerFamily: String?
    let durationMilliseconds: Int64?
    let reasonCode: String?
    let producer: String
}

/// What the app plays for a cited media source: a verified local file or an online video's embed.
struct MediaPlayback: Decodable, Equatable {
    let sourceId: String
    let name: String
    let kind: String
    let path: String?
    let url: String?
    let mimeType: String
    let isVideo: Bool
}

struct MediaPlaybackData: Decodable {
    let playback: MediaPlayback
}

/// A cited time range in media, from a `time:START-END` locator (seconds).
struct MediaTimeRange: Equatable {
    let start: Double
    let end: Double

    init?(locator: String) {
        guard locator.hasPrefix("time:") else { return nil }
        let parts = locator.dropFirst(5).split(separator: "-")
        guard parts.count == 2, let start = Double(parts[0]), let end = Double(parts[1]), start >= 0, end >= start else { return nil }
        self.start = start
        self.end = end
    }

    static func clock(_ seconds: Double) -> String {
        let total = Int(seconds.rounded(.down))
        let hours = total / 3600, minutes = (total % 3600) / 60, rest = total % 60
        return hours > 0 ? String(format: "%d:%02d:%02d", hours, minutes, rest) : String(format: "%d:%02d", minutes, rest)
    }

    var label: String { "\(Self.clock(start))–\(Self.clock(end))" }
}

struct MediaPlaybackError: Error {
    let message: String
}
