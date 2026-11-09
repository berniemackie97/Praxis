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
