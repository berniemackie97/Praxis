import AVFoundation
import AVKit
import SwiftUI
import WebKit

/// A cited moment of a video or recording, played inline in chat: it starts at the cited time and
/// stops at the cited end, with the system's playback controls, a switch to the full recording and a
/// way to open it in QuickTime Player. Online videos play through the provider's embed.
struct MediaClipCard: View {
    let number: Int
    let citation: AgentCitation
    let range: MediaTimeRange
    @ObservedObject var model: ChatWorkbenchModel

    @State private var playback: MediaPlayback?
    @State private var failure: String?
    @State private var full = false
    @State private var webLoaded = false
    @State private var playableRange: MediaTimeRange?
    @State private var loading = false

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack(spacing: Spacing.s) {
                Text("\(number)").font(.meta.monospacedDigit().weight(.semibold))
                    .padding(.horizontal, Spacing.xs + 1).padding(.vertical, 1)
                    .background(Capsule().fill(Color.accentColor.opacity(0.18)))
                Image(systemName: playback?.isVideo == false ? "waveform" : "film").foregroundStyle(.secondary)
                Text(citation.sourceName).font(.rowTitle).lineLimit(1).truncationMode(.middle)
                Text(full ? "Full recording" : (playableRange ?? range).label).font(.meta.monospacedDigit()).foregroundStyle(.secondary)
                Spacer(minLength: Spacing.s)
                if playback != nil {
                    Button(full ? "Clip Only" : "Play Full Video") { full.toggle() }
                        .buttonStyle(.link).font(.meta)
                        .help(full ? "Limit playback to the cited moment" : "Keep playing past the cited moment")
                }
                if let path = playback?.path {
                    Button {
                        openInQuickTime(path)
                    } label: {
                        Image(systemName: "arrow.up.forward.app")
                    }
                    .buttonStyle(.borderless)
                    .help("Open in QuickTime Player")
                }
            }
            player
        }
        .padding(Spacing.m)
        .background(RoundedRectangle(cornerRadius: Radius.m).fill(Surface.card))
        .overlay(RoundedRectangle(cornerRadius: Radius.m).strokeBorder(Surface.stroke.opacity(0.6)))

    }

    @ViewBuilder private var player: some View {
        if let playback, playback.kind == "file", let path = playback.path, let playableRange {
            ClipPlayerView(url: URL(fileURLWithPath: path), mimeType: playback.mimeType, range: playableRange, full: full)
                .frame(height: playback.isVideo ? 300 : 56)
                .clipShape(RoundedRectangle(cornerRadius: Radius.s))
        } else if let playback, playback.kind == "web", let url = playback.url.flatMap(URL.init(string:)) {
            VStack(alignment: .leading, spacing: Spacing.s) {
                if webLoaded {
                    WebClipView(url: Self.embed(url, range: range, full: full))
                        .frame(height: 300)
                        .clipShape(RoundedRectangle(cornerRadius: Radius.s))
                } else {
                    Button("Load online clip") { webLoaded = true }
                        .help("Connect to the video provider to load its embedded player.")
                }
                if let original = Self.watchURL(url, startingAt: range.start) {
                    Link("Open in browser at \(MediaTimeRange.clock(range.start))", destination: original)
                        .font(.meta)
                }
            }
        } else if let failure {
            Label(failure, systemImage: "exclamationmark.triangle").font(.meta).foregroundStyle(.secondary)
        } else if loading {
            HStack(spacing: Spacing.s) {
                ProgressView().controlSize(.small)
                Text("Preparing clip…").font(.meta).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, minHeight: 56)
        } else {
            Button { load() } label: { Label("Open Clip", systemImage: "play.circle") }
        }
    }

    private func load() {
        guard playback == nil, failure == nil, !loading else { return }
        loading = true
        model.playback(for: citation) { result in
            switch result {
            case .success(let value): prepare(value)
            case .failure(let error): failure = error.message
            }
        }
    }

    private func prepare(_ value: MediaPlayback) {
        guard value.kind == "file", let path = value.path else {
            playback = value
            return
        }

        Task { @MainActor in
            let asset = AVURLAsset(url: URL(fileURLWithPath: path), options: ["AVURLAssetOutOfBandMIMETypeKey": value.mimeType])
            do {
                guard try await asset.load(.isPlayable) else {
                    failure = "This recording’s codec cannot be played on this Mac."
                    return
                }
                let duration = try await asset.load(.duration).seconds
                let offset: Double
                if citation.playbackTimebase == "audio-sample-v1" {
                    guard let mapped = try await Self.audioSampleOrigin(asset) else {
                        failure = "The recording has an edited audio timeline. Use matching captions to play an exact clip."
                        return
                    }
                    offset = mapped
                } else {
                    offset = 0
                }

                let start = range.start + offset
                guard duration.isFinite, duration > start else {
                    failure = "The cited start is outside the playable recording. Ask for a fresh clip."
                    return
                }
                playableRange = MediaTimeRange(start: start, end: min(range.end + offset, duration))
                playback = value
            } catch {
                failure = "The recording could not be opened for playback."
            }
        }
    }

    /// Apple Speech transcribes AVAudioFile samples starting at zero. Map that origin to the
    /// playable timeline without guessing across non-linear edits, alternate tracks, or audio trims.
    static func audioSampleOrigin(_ asset: AVAsset) async throws -> Double? {
        let tracks = try await asset.loadTracks(withMediaType: .audio)
        guard tracks.count == 1, let track = tracks.first else { return nil }
        let segments = try await track.load(.segments).filter { !$0.isEmpty }
        guard segments.count == 1, let mapping = segments.first?.timeMapping,
              abs(mapping.source.start.seconds) <= 0.002,
              abs(mapping.source.duration.seconds - mapping.target.duration.seconds) <= 0.002,
              mapping.target.start.seconds.isFinite, mapping.target.start.seconds >= 0 else { return nil }
        return mapping.target.start.seconds
    }

    static func watchURL(_ url: URL, startingAt seconds: Double) -> URL? {
        guard let host = url.host?.lowercased(), ["www.youtube.com", "www.youtube-nocookie.com", "youtube.com", "youtube-nocookie.com"].contains(host),
              url.pathComponents.count == 3, url.pathComponents[1] == "embed" else { return nil }
        var components = URLComponents()
        components.scheme = "https"
        components.host = "www.youtube.com"
        components.path = "/watch"
        components.queryItems = [URLQueryItem(name: "v", value: url.lastPathComponent), URLQueryItem(name: "t", value: "\(Int(seconds))s")]
        return components.url
    }

    private func openInQuickTime(_ path: String) {
        let url = URL(fileURLWithPath: path)
        let configuration = NSWorkspace.OpenConfiguration()
        if let quickTime = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.QuickTimePlayerX") {
            NSWorkspace.shared.open([url], withApplicationAt: quickTime, configuration: configuration)
        } else {
            NSWorkspace.shared.open(url)
        }
    }

    /// The provider's embed limited to the cited range; the worker returns embed URLs only for known hosts.
    static func embed(_ url: URL, range: MediaTimeRange, full: Bool) -> URL {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return url }
        var items = (components.queryItems ?? []).filter { !["start", "end", "autoplay"].contains($0.name) }
        items.append(URLQueryItem(name: "start", value: String(Int(range.start))))
        if !full { items.append(URLQueryItem(name: "end", value: String(Int(range.end.rounded(.up))))) }
        components.queryItems = items
        return components.url ?? url
    }
}

/// AVKit's player view, bounded to a time range unless `full` is set. Reaching the end of the clip
/// pauses at the end; pressing play again restarts the clip.
private struct ClipPlayerView: NSViewRepresentable {
    let url: URL
    let mimeType: String
    let range: MediaTimeRange
    let full: Bool

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .inline
        view.showsFullScreenToggleButton = true
        let asset = AVURLAsset(url: url, options: ["AVURLAssetOutOfBandMIMETypeKey": mimeType])
        let item = AVPlayerItem(asset: asset)
        let player = AVPlayer(playerItem: item)
        view.player = player
        context.coordinator.attach(player: player, range: range)
        apply(item, full: full)
        player.seek(to: CMTime(seconds: range.start, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        guard let item = view.player?.currentItem else { return }
        apply(item, full: full)
    }

    static func dismantleNSView(_ view: AVPlayerView, coordinator: Coordinator) {
        view.player?.pause()
        coordinator.detach()
    }

    private func apply(_ item: AVPlayerItem, full: Bool) {
        item.forwardPlaybackEndTime = full ? .invalid : CMTime(seconds: range.end, preferredTimescale: 600)
    }

    @MainActor
    final class Coordinator {
        private var observer: (any NSObjectProtocol)?
        private var rateObserver: NSKeyValueObservation?
        private weak var player: AVPlayer?
        private var start = CMTime.zero
        private var finished = false
        private var observationGeneration = UUID()

        func attach(player: AVPlayer, range: MediaTimeRange) {
            self.player = player
            start = CMTime(seconds: range.start, preferredTimescale: 600)
            let generation = UUID()
            observationGeneration = generation
            observer = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime,
                                                              object: player.currentItem, queue: .main) { [weak self] _ in
                Task { @MainActor [weak self] in
                    guard let self, observationGeneration == generation else { return }
                    finished = true
                }
            }
            rateObserver = player.observe(\.rate, options: [.new]) { [weak self] _, change in
                guard change.newValue ?? 0 > 0 else { return }
                Task { @MainActor [weak self] in
                    guard let self, observationGeneration == generation, finished,
                          let player = self.player else { return }
                    finished = false
                    player.seek(to: start, toleranceBefore: .zero, toleranceAfter: .zero)
                }
            }
        }

        func detach() {
            observationGeneration = UUID()
            if let observer { NotificationCenter.default.removeObserver(observer) }
            rateObserver?.invalidate()
            observer = nil
            rateObserver = nil
            player = nil
            finished = false
        }
    }
}

/// An online video's privacy-enhanced embed. Loaded only when the owner opens the clip.
private struct WebClipView: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.mediaTypesRequiringUserActionForPlayback = []
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.load(URLRequest(url: url))
        return view
    }

    func updateNSView(_ view: WKWebView, context: Context) {
        if view.url?.absoluteString != url.absoluteString { view.load(URLRequest(url: url)) }
    }
}
