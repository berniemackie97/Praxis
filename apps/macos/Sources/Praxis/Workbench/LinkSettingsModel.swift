import Combine
import Foundation

struct LinkSettings: Decodable, Equatable {
    let mode: String
    let blockedHosts: [String]
    var studyMedia: Bool? = nil
    var sampleFrames: Bool? = nil
    var visionModel: String? = nil
    var maximumMediaMinutes: Int? = nil

    static let modes = ["saved_links", "off"]
}

struct LinkSettingsData: Decodable {
    let settings: LinkSettings
}

struct SavedLinkData: Decodable {
    let url: String
    let fileName: String
}

/// Saved links: what Praxis may fetch for links the owner saves, and saving a link.
@MainActor
final class LinkSettingsModel: ObservableObject {
    private let channel: WorkerChannel

    @Published private(set) var settings: LinkSettings?
    @Published var presentingSave = false
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    func refresh() {
        channel.send("linkSettings", as: LinkSettingsData.self, failed: report) { [weak self] data in
            self?.settings = data.settings
        }
    }

    func save(mode: String, blockedHosts: [String], studyMedia: Bool? = nil, sampleFrames: Bool? = nil,
              visionModel: String? = nil, maximumMediaMinutes: Int? = nil) {
        var fields: [String: Any] = ["mode": mode, "blockedHosts": blockedHosts]
        if let studyMedia { fields["studyMedia"] = studyMedia }
        if let sampleFrames { fields["sampleFrames"] = sampleFrames }
        if let visionModel {
            if visionModel.isEmpty { fields["clearVisionModel"] = true } else { fields["visionModel"] = visionModel }
        }
        if let maximumMediaMinutes { fields["maximumMediaMinutes"] = maximumMediaMinutes }
        channel.send("setLinkSettings", fields, as: LinkSettingsData.self,
                     failed: report) { [weak self] data in
            self?.settings = data.settings
            self?.notice = "Link settings saved."
        }
    }

    /// Saves a link the owner typed; Praxis fetches and reads it in the background.
    func saveLink(_ url: String, title: String, completion: @escaping (Bool) -> Void) {
        var fields: [String: Any] = ["url": url.trimmingCharacters(in: .whitespacesAndNewlines)]
        let name = title.trimmingCharacters(in: .whitespacesAndNewlines)
        if !name.isEmpty { fields["title"] = name }
        let sent = channel.send("saveLink", fields, as: SavedLinkData.self, failed: { [weak self] message in
            self?.report(message)
            completion(false)
        }) { [weak self] data in
            self?.notice = "Saved \(data.url). Praxis reads it in the background."
            completion(true)
        }
        if !sent { completion(false) }
    }

    /// Host names one per line or separated by commas, lowercased and without schemes or paths.
    static func hosts(from text: String) -> [String] {
        text.split(whereSeparator: { $0 == "\n" || $0 == "," || $0 == " " })
            .map { part -> String in
                var host = part.trimmingCharacters(in: .whitespaces).lowercased()
                if let url = URL(string: host), let parsed = url.host { host = parsed }
                return host
            }
            .filter { !$0.isEmpty }
            .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
    }

    private func report(_ message: String) { error = message }
}
