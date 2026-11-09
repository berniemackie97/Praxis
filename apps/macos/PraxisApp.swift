import AppKit
import Combine
import Foundation
import SwiftUI

private struct LibraryItem: Decodable, Identifiable {
    let sourceId: String
    let itemId: String
    let sourceKind: String
    let sourceLocator: String
    let sourceStatus: String
    let revision: Int
    let sha256: String
    let originalBytes: Int64
    let processingState: String?
    let extractionOutcome: String?
    var id: String { itemId }
    var title: String { URL(fileURLWithPath: sourceLocator).lastPathComponent }
}

private struct SearchHit: Decodable, Identifiable {
    let sourceId: String
    let itemId: String
    let revision: Int
    let title: String
    let sourceLocator: String
    let originalSha256: String
    let snippet: String
    var id: String { "\(itemId)-\(revision)" }
}

private struct ResponseData: Decodable {
    let items: [LibraryItem]?
    let hits: [SearchHit]?
    let outcome: String?
    let content: String?
    let reasonCode: String?
    let verified: Bool?
    let queued: Bool?
    let revoked: Bool?
}

private struct Response: Decodable {
    let requestId: String?
    let ok: Bool
    let data: ResponseData?
    let error: String?
}

@MainActor
private final class Workbench: ObservableObject {
    @Published private(set) var items: [LibraryItem] = []
    @Published private(set) var hits: [SearchHit] = []
    @Published private(set) var selected: LibraryItem?
    @Published private(set) var preview = ""
    @Published private(set) var message = "Select a private catalog and watched Inbox."
    @Published private(set) var running = false
    @Published var query = ""
    @Published var page = 0

    private var child: Process?
    private var input: FileHandle?
    private var buffer = Data()
    private var requests: [String: String] = [:]

    func start(dataRoot: String, inbox: String, initialize: Bool) {
        guard !running else { return }
        let data = URL(fileURLWithPath: dataRoot).standardizedFileURL.resolvingSymlinksInPath()
        let source = URL(fileURLWithPath: inbox).standardizedFileURL.resolvingSymlinksInPath()
        guard data.path != source.path,
              !data.path.hasPrefix(source.path + "/"),
              !source.path.hasPrefix(data.path + "/") else {
            message = "Catalog and watched Inbox must not overlap."
            return
        }
        guard FileManager.default.fileExists(atPath: source.path) else {
            message = "The selected Inbox is unavailable. Reconnect it or choose another."
            return
        }
        do {
            if initialize && !FileManager.default.fileExists(atPath: data.path) {
                try FileManager.default.createDirectory(at: data,
                    withIntermediateDirectories: false,
                    attributes: [.posixPermissions: 0o700])
            }
            guard FileManager.default.fileExists(atPath: data.path) else {
                message = "The configured catalog is missing. Reconnect or restore it."
                return
            }
            guard let worker = Bundle.main.resourceURL?
                .appendingPathComponent("worker/Praxis.Worker.dll"),
                FileManager.default.fileExists(atPath: worker.path) else {
                message = "Praxis.Worker.dll is missing from the app bundle."
                return
            }
            let candidates = [
                ProcessInfo.processInfo.environment["DOTNET_ROOT"].map { $0 + "/dotnet" },
                "/usr/local/share/dotnet/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/local/bin/dotnet"
            ].compactMap { $0 }
            guard let executable = candidates.first(where: {
                FileManager.default.isExecutableFile(atPath: $0)
            }) else {
                message = "Install the .NET 10 runtime and reopen Praxis."
                return
            }

            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = [worker.path, "--data-root", data.path,
                                 "--intake", source.path, "--stdio"]
            let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
            process.standardInput = stdin
            process.standardOutput = stdout
            process.standardError = stderr
            stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let bytes = handle.availableData
                if bytes.isEmpty {
                    handle.readabilityHandler = nil
                    return
                }
                DispatchQueue.main.async { self?.receive(bytes) }
            }
            stderr.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let bytes = handle.availableData
                if bytes.isEmpty {
                    handle.readabilityHandler = nil
                    return
                }
                DispatchQueue.main.async {
                    self?.message = String(decoding: bytes.prefix(2048), as: UTF8.self)
                }
            }
            process.terminationHandler = { [weak self] process in
                DispatchQueue.main.async {
                    self?.running = false
                    if process.terminationStatus != 0 {
                        self?.message = "The worker exited (\(process.terminationStatus)). Check the private catalog."
                    }
                }
            }
            try process.run()
            child = process
            input = stdin.fileHandleForWriting
            running = true
            message = "Monitoring your approved Inbox. Files are captured automatically."
            refresh()
        } catch {
            message = "Praxis could not start: \(error.localizedDescription)"
        }
    }

    func refresh() {
        if running { send("library", ["limit": 100, "offset": page * 100]) }
    }

    func search() {
        if !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            send("search", ["query": query])
        } else {
            hits = []
        }
    }

    func select(_ item: LibraryItem) {
        selected = item
        preview = "Loading extracted text…"
        send("preview", ["itemId": item.itemId, "revision": item.revision])
    }

    func previewHit(_ hit: SearchHit) {
        preview = "Loading extracted text…"
        send("preview", ["itemId": hit.itemId, "revision": hit.revision])
    }

    func verify() {
        if let selected { send("verify", ["itemId": selected.itemId]) }
    }

    func retry() {
        if let selected {
            send("retry", ["itemId": selected.itemId, "revision": selected.revision])
        }
    }

    func revoke() {
        if let selected { send("revoke", ["sourceId": selected.sourceId]) }
    }

    func stop() {
        guard running else { return }
        send("stop", [:])
        input?.closeFile()
        input = nil
    }

    private func send(_ action: String, _ fields: [String: Any]) {
        guard let input, running else { return }
        let id = UUID().uuidString
        var request = fields
        request["requestId"] = id
        request["action"] = action
        guard let encoded = try? JSONSerialization.data(withJSONObject: request),
              encoded.count < 16_000 else {
            message = "The request exceeds the private protocol limit."
            return
        }
        do {
            requests[id] = action
            try input.write(contentsOf: encoded + Data([0x0A]))
        } catch {
            requests.removeValue(forKey: id)
            message = "The worker is no longer accepting requests."
        }
    }

    private func receive(_ bytes: Data) {
        guard !bytes.isEmpty else { return }
        buffer.append(bytes)
        if buffer.count > 2_000_000 {
            buffer.removeAll()
            message = "A response exceeded the UI memory budget."
            return
        }
        while let end = buffer.firstIndex(of: 0x0A) {
            let line = Data(buffer[..<end])
            buffer.removeSubrange(...end)
            guard let reply = try? JSONDecoder().decode(Response.self, from: line),
                  let id = reply.requestId,
                  let action = requests.removeValue(forKey: id) else { continue }
            guard reply.ok else {
                message = reply.error ?? "The operation failed."
                continue
            }
            switch action {
            case "library":
                items = reply.data?.items ?? []
                if let previous = selected {
                    selected = items.first(where: { $0.itemId == previous.itemId })
                }
            case "search": hits = reply.data?.hits ?? []
            case "preview":
                preview = reply.data?.content ??
                    "No extracted text (\(reply.data?.reasonCode ?? reply.data?.outcome ?? "pending")). The original remains preserved."
            case "verify":
                message = reply.data?.verified == true
                    ? "Original integrity verified against stored SHA-256." : "Verification incomplete."
            case "retry":
                message = reply.data?.queued == true
                    ? "Processing retry queued." : "This source is not eligible for retry."
                refresh()
            case "revoke":
                message = reply.data?.revoked == true
                    ? "Source removed from active retrieval; original bytes retained."
                    : "This source is not active."
                refresh()
            default: break
            }
        }
    }
}

@main
struct PraxisApp: App {
    @StateObject private var model = Workbench()
    var body: some Scene {
        Window("Praxis", id: "main") {
            PraxisWindow()
                .environmentObject(model)
                .frame(minWidth: 880, minHeight: 600)
        }
    }
}

private struct PraxisWindow: View {
    @EnvironmentObject private var model: Workbench
    @AppStorage("dataRoot") private var dataRoot = ""
    @AppStorage("inbox") private var inbox = ""
    @AppStorage("configured") private var configured = false
    @State private var section = 0
    @State private var confirmRevoke = false
    private let refreshTimer = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading) {
                    Text("Praxis").font(.largeTitle.weight(.semibold))
                    Text("Local knowledge workbench").foregroundStyle(.secondary)
                }
                Spacer()
                Text(model.running ? "Intake active" : "Not running")
                    .foregroundColor(model.running ? Color.green : Color.secondary)
            }
            if model.running {
                Picker("Workspace", selection: $section) {
                    Text("Inbox & Library").tag(0)
                    Text("Search").tag(1)
                    Text("Configuration").tag(2)
                }.pickerStyle(.segmented)
                if section == 0 { library }
                if section == 1 { search }
                if section == 2 { configuration }
            } else {
                setup
            }
            Divider()
            Text(model.message).font(.footnote).foregroundStyle(.secondary)
                .lineLimit(3).textSelection(.enabled)
        }
        .padding(20)
        .onAppear {
            if configured && !dataRoot.isEmpty && !inbox.isEmpty {
                model.start(dataRoot: dataRoot, inbox: inbox, initialize: false)
            }
        }
        .onDisappear { model.stop() }
        .onReceive(refreshTimer) { _ in
            if model.running { model.refresh() }
        }
        .confirmationDialog("Revoke this source from active retrieval?",
                            isPresented: $confirmRevoke) {
            Button("Revoke source", role: .destructive) { model.revoke() }
        } message: {
            Text("Search and derived text will no longer expose this source. Its stored original bytes are retained.")
        }
    }

    private var setup: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Set up your private Second Brain").font(.title2)
            Text("Choose a parent for a private PraxisData folder and a separate existing Inbox. Use folders outside cloud-sync services if you do not want OS-level synchronization.")
                .foregroundStyle(.secondary)
            HStack {
                Button("Choose catalog location…") {
                    if let selected = chooseFolder() {
                        dataRoot = selected.appendingPathComponent("PraxisData").path
                        configured = false
                    }
                }
                Text(dataRoot.isEmpty ? "Not selected" : dataRoot)
                    .textSelection(.enabled).lineLimit(2)
            }
            HStack {
                Button("Choose watched Inbox…") {
                    if let selected = chooseFolder() {
                        inbox = selected.path
                        configured = false
                    }
                }
                Text(inbox.isEmpty ? "Not selected" : inbox)
                    .textSelection(.enabled).lineLimit(2)
            }
            Button("Start Praxis") {
                model.start(dataRoot: dataRoot, inbox: inbox, initialize: !configured)
                if model.running { configured = true }
            }
            .buttonStyle(.borderedProminent)
            .disabled(dataRoot.isEmpty || inbox.isEmpty)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private var library: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack {
                HStack {
                    Text("Captured sources").font(.headline)
                    Spacer()
                    Button("Refresh") { model.refresh() }
                }
                List(model.items) { item in
                    Button { model.select(item) } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(item.title).fontWeight(.medium)
                            Text("Revision \(item.revision) · \(item.processingState ?? "pending") · \(item.extractionOutcome ?? "not extracted")")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }.buttonStyle(.plain)
                }
                HStack {
                    Button("Previous") {
                        model.page -= 1
                        model.refresh()
                    }.disabled(model.page == 0)
                    Spacer()
                    Text("Page \(model.page + 1)").font(.caption)
                    Spacer()
                    Button("Next") {
                        model.page += 1
                        model.refresh()
                    }.disabled(model.items.count < 100)
                }
            }.frame(width: 310)
            Divider()
            VStack(alignment: .leading, spacing: 10) {
                if let item = model.selected {
                    Text(item.title).font(.title3.weight(.semibold))
                    Text(item.sourceLocator).font(.caption).textSelection(.enabled)
                    Text("Source \(item.sourceId) · Revision \(item.revision)")
                        .font(.caption).textSelection(.enabled)
                    Text("Original \(item.originalBytes) bytes · SHA-256 \(item.sha256)")
                        .font(.caption).textSelection(.enabled)
                    Text("State: \(item.sourceStatus) / \(item.processingState ?? "pending") / \(item.extractionOutcome ?? "unknown")")
                        .font(.caption)
                    HStack {
                        Button("Verify original") { model.verify() }
                        Button("Retry extraction") { model.retry() }
                        Button("Revoke…", role: .destructive) { confirmRevoke = true }
                    }
                    Text("Extracted text preview").font(.headline)
                    ScrollView {
                        Text(model.preview)
                            .font(.system(.body, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                } else {
                    Text("Select a captured source to inspect it.")
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var search: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                TextField("Search extracted text", text: $model.query)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { model.search() }
                Button("Search") { model.search() }
            }
            HStack(alignment: .top) {
                List(model.hits) { hit in
                    Button { model.previewHit(hit) } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(hit.title).fontWeight(.medium)
                            Text(hit.sourceLocator).font(.caption).foregroundStyle(.secondary)
                            Text(hit.snippet).font(.callout).lineLimit(3)
                        }
                    }.buttonStyle(.plain)
                }.frame(minWidth: 350)
                Divider()
                ScrollView {
                    Text(model.preview).font(.system(.body, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
            }
        }
    }

    private var configuration: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Private catalog").font(.headline)
            Text(dataRoot).textSelection(.enabled)
            Text("Approved watched folder").font(.headline)
            Text(inbox).textSelection(.enabled)
            Text("No browser service, remote model, connector or agent credential is enabled.")
                .foregroundStyle(.secondary)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private func chooseFolder() -> URL? {
        let picker = NSOpenPanel()
        picker.canChooseDirectories = true
        picker.canChooseFiles = false
        picker.allowsMultipleSelection = false
        picker.canCreateDirectories = true
        return picker.runModal() == .OK ? picker.url : nil
    }
}
