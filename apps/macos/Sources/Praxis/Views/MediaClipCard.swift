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

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack(spacing: Spacing.s) {
                Text("\(number)").font(.meta.monospacedDigit().weight(.semibold))
                    .padding(.horizontal, Spacing.xs + 1).padding(.vertical, 1)
                    .background(Capsule().fill(Color.accentColor.opacity(0.18)))
                Image(systemName: playback?.isVideo == false ? "waveform" : "film").foregroundStyle(.secondary)
                Text(citation.sourceName).font(.rowTitle).lineLimit(1).truncationMode(.middle)
                Text(full ? "Full recording" : range.label).font(.meta.monospacedDigit()).foregroundStyle(.secondary)
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
        .onAppear(perform: load)
    }

    @ViewBuilder private var player: some View {
        if let playback, playback.kind == "file", let path = playback.path {
            ClipPlayerView(url: URL(fileURLWithPath: path), mimeType: playback.mimeType, range: range, full: full)
                .frame(height: playback.isVideo ? 300 : 56)
                .clipShape(RoundedRectangle(cornerRadius: Radius.s))
        } else if let playback, playback.kind == "web", let url = playback.url.flatMap(URL.init(string:)) {
            WebClipView(url: Self.embed(url, range: range, full: full))
                .frame(height: 300)
                .clipShape(RoundedRectangle(cornerRadius: Radius.s))
        } else if let failure {
            Label(failure, systemImage: "exclamationmark.triangle").font(.meta).foregroundStyle(.secondary)
        } else {
            HStack(spacing: Spacing.s) {
                ProgressView().controlSize(.small)
                Text("Preparing clip…").font(.meta).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, minHeight: 56)
        }
    }

    private func load() {
        guard playback == nil, failure == nil else { return }
        model.playback(for: citation.sourceId) { result in
            switch result {
            case .success(let value): playback = value
            case .failure(let error): failure = error.message
            }
        }
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

    final class Coordinator {
        private var observer: (any NSObjectProtocol)?
        private var rateObserver: NSKeyValueObservation?
        private var finished = false

        func attach(player: AVPlayer, range: MediaTimeRange) {
            observer = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: player.currentItem,
                                                              queue: .main) { [weak self] _ in
                self?.finished = true
            }
            // Play after the clip ended starts the clip over instead of doing nothing.
            rateObserver = player.observe(\.rate, options: [.new]) { [weak self] player, change in
                guard let self, change.newValue ?? 0 > 0, finished else { return }
                finished = false
                player.seek(to: CMTime(seconds: range.start, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
            }
        }

        func detach() {
            if let observer { NotificationCenter.default.removeObserver(observer) }
            rateObserver?.invalidate()
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
