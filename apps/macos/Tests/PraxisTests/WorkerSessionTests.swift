import Foundation
import Testing
@testable import Praxis

/// End-to-end check of the worker transport against the published .NET worker. Runs when
/// `PRAXIS_WORKER_DLL` names the worker (scripts/macos/test-worker-session.sh sets it).
enum WorkerFixture {
    static let workerPath: String? = {
        let files = FileManager.default
        let explicit = ProcessInfo.processInfo.environment["PRAXIS_WORKER_DLL"]
        let bundled = Bundle.main.resourceURL?
            .appendingPathComponent("worker/Praxis.Worker.dll").path
        return [explicit, bundled].compactMap { $0 }
            .first(where: { files.fileExists(atPath: $0) })
    }()

    static let dotnetPath: String? = {
        let files = FileManager.default
        let environment = ProcessInfo.processInfo.environment
        let candidates = [
            environment["PRAXIS_DOTNET"],
            environment["DOTNET_ROOT"].map { $0 + "/dotnet" },
            files.homeDirectoryForCurrentUser.appendingPathComponent(".dotnet/dotnet").path,
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/opt/homebrew/opt/dotnet/libexec/dotnet",
            "/usr/local/bin/dotnet"
        ].compactMap { $0 }
        return candidates.first(where: { files.isExecutableFile(atPath: $0) })
    }()
}

@MainActor
@Suite("Bundled worker transport")
struct WorkerSessionTests {
    @Test("Capture, search, typed replies, denied requests, restart and malformed responses")
    func workerTransport() async throws {
        let executable = URL(fileURLWithPath: try #require(WorkerFixture.dotnetPath))
        let worker = try #require(WorkerFixture.workerPath)
        let fixture = FileManager.default.temporaryDirectory
            .appendingPathComponent("praxis-worker-smoke-\(UUID().uuidString)")
        let catalog = fixture.appendingPathComponent("Catalog")
        let inbox = fixture.appendingPathComponent("Inbox")
        let mode: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: false,
                                                attributes: mode)
        defer { try? FileManager.default.removeItem(at: fixture) }
        try FileManager.default.createDirectory(at: catalog, withIntermediateDirectories: false,
                                                attributes: mode)
        try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: false,
                                                attributes: mode)
        let source = inbox.appendingPathComponent("fixture.md")
        try "# Worker smoke\n\nPraxisTransportFixture\n".write(to: source, atomically: true,
                                                                encoding: .utf8)

        let session = WorkerSession()
        var exited = false
        var exitError: String?

        func startWorker() throws {
            exited = false
            exitError = nil
            try session.start(
                executable: executable,
                arguments: [worker, "--data-root", catalog.path, "--intake", inbox.path, "--stdio"],
                onDiagnostic: { _ in },
                onExit: { error in
                    exitError = error
                    exited = true
                }
            )
        }

        func search() async throws -> [SearchHit] {
            var response: WorkerResponse?
            try session.send("search", fields: ["query": "PraxisTransportFixture"]) { response = $0 }
            try await waitFor("search response") { response != nil }
            guard let response, response.ok, let hits = response.data?.hits else {
                throw Failure.unexpected(response?.error ?? "Missing search results")
            }
            return hits
        }

        try startWorker()
        let deadline = ContinuousClock.now + .seconds(15)
        var hits = try await search()
        while hits.isEmpty && ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(100))
            hits = try await search()
        }
        guard hits.count == 1, let firstHit = hits.first, firstHit.revision == 1 else {
            throw Failure.unexpected("Expected one indexed revision from watched intake")
        }

        var denied: WorkerResponse?
        try session.send("verify", fields: ["itemId": "invalid-id"]) { denied = $0 }
        try await waitFor("validation response") { denied != nil }
        guard denied?.ok == false else { throw Failure.unexpected("Invalid item was accepted") }

        // Typed replies: feature payloads decode without the shared response shape, and a
        // decoding mismatch fails the session instead of silently dropping data.
        var profile: WorkerReply<ModelProfileData>?
        try session.send("modelProfileSave", fields: [
            "provider": "ollama", "endpoint": "http://127.0.0.1:11434", "model": "smoke:1b",
            "contextTokens": 4096, "enabled": false
        ], as: ModelProfileData.self) { profile = $0 }
        try await waitFor("model profile") { profile != nil }
        guard profile?.data?.profile?.model == "smoke:1b", profile?.data?.profile?.enabled == false else {
            throw Failure.unexpected("Model profile did not round-trip through a typed reply")
        }

        var providers: WorkerReply<ProvidersData>?
        try session.send("verificationProviders", fields: [:], as: ProvidersData.self) { providers = $0 }
        try await waitFor("verification providers") { providers != nil }
        guard providers?.ok == true, providers?.data?.providers.isEmpty == true else {
            throw Failure.unexpected("Verification providers did not decode")
        }

        var entities: WorkerReply<EntityListData>?
        try session.send("entities", fields: ["limit": 10], as: EntityListData.self) { entities = $0 }
        var requests: WorkerReply<VerificationListData>?
        try session.send("verifications", fields: [:], as: VerificationListData.self) { requests = $0 }
        try await waitFor("knowledge and verification lists") { entities != nil && requests != nil }
        guard entities?.data?.entities.isEmpty == true, requests?.data?.requests.isEmpty == true else {
            throw Failure.unexpected("Knowledge or verification lists did not decode")
        }

        var graphOverview: WorkerReply<GraphOverview>?
        try session.send("graphOverview", fields: [:], as: GraphOverview.self) { graphOverview = $0 }
        try await waitFor("unified graph overview") { graphOverview != nil }
        guard graphOverview?.ok == true, graphOverview?.data?.entityCount == 0,
              graphOverview?.data?.types.isEmpty == true else {
            throw Failure.unexpected("Unified overview did not decode an empty authorized catalog")
        }

        var graphSeeds: WorkerReply<GraphPage>?
        try session.send("graphSeeds", fields: ["limit": 10], as: GraphPage.self) {
            graphSeeds = $0
        }
        try await waitFor("graph seed projection") { graphSeeds != nil }
        guard graphSeeds?.ok == true, graphSeeds?.data?.outcome == "empty",
              graphSeeds?.data?.nodes.isEmpty == true else {
            throw Failure.unexpected("Graph seed action did not decode an empty catalog")
        }

        var graphNeighborhood: WorkerReply<GraphPage>?
        try session.send("graphNeighborhood", fields: ["entityId": UUID().uuidString.lowercased()],
                         as: GraphPage.self) { graphNeighborhood = $0 }
        try await waitFor("graph neighborhood projection") { graphNeighborhood != nil }
        guard graphNeighborhood?.ok == true, graphNeighborhood?.data?.outcome == "unavailable" else {
            throw Failure.unexpected("Graph neighborhood did not deny an unknown entity")
        }

        var graphRelation: WorkerReply<GraphPage>?
        try session.send("graphRelation", fields: [
            "entityId": UUID().uuidString.lowercased(), "relationId": UUID().uuidString.lowercased()
        ], as: GraphPage.self) { graphRelation = $0 }
        try await waitFor("graph relation inspection") { graphRelation != nil }
        guard graphRelation?.ok == true, graphRelation?.data?.outcome == "unavailable",
              graphRelation?.data?.edges.isEmpty == true else {
            throw Failure.unexpected("Graph inspection revealed a missing relationship")
        }

        var rejected: WorkerReply<VerificationCreatedData>?
        try session.send("verificationApprove", fields: ["verificationId": UUID().uuidString.lowercased()],
                         as: VerificationCreatedData.self) { rejected = $0 }
        try await waitFor("rejected approval") { rejected != nil }
        guard rejected?.ok == false, rejected?.error != nil else {
            throw Failure.unexpected("Approving an unknown verification was accepted")
        }

        session.stop()
        try await waitFor("worker exit") { exited }
        if let exitError { throw Failure.unexpected(exitError) }

        try startWorker()
        let reopened = try await search()
        guard reopened.map(\.id) == [firstHit.id] else {
            throw Failure.unexpected("Search identity changed after restart")
        }
        session.stop()
        try await waitFor("restarted worker exit") { exited }
        if let exitError { throw Failure.unexpected(exitError) }

        exited = false
        try session.start(
            executable: URL(fileURLWithPath: "/bin/sh"),
            arguments: ["-c", "read -r request; printf '%s\\n' invalid; read -r next"],
            onDiagnostic: { _ in },
            onExit: { error in
                exitError = error
                exited = true
            }
        )
        var receivedMalformedReply = false
        try session.send("library", fields: [:]) { _ in receivedMalformedReply = true }
        try await waitFor("malformed response handling") { exited }
        guard !receivedMalformedReply, exitError == "The worker returned an invalid response." else {
            throw Failure.unexpected("Malformed response did not fail the session")
        }

    }

    private func waitFor(_ event: String, condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now + .seconds(5)
        while !condition() {
            guard ContinuousClock.now < deadline else { throw Failure.timeout(event) }
            try await Task.sleep(for: .milliseconds(25))
        }
    }

    private enum Failure: Error {
        case unexpected(String)
        case timeout(String)
    }
}
