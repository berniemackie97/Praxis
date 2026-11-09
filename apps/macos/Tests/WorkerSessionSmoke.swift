import Foundation

@main
@MainActor
struct WorkerSessionSmoke {
    static func main() async throws {
        let executable = URL(fileURLWithPath: CommandLine.arguments[1])
        let worker = CommandLine.arguments[2]
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
            try session.send(.search, fields: ["query": "PraxisTransportFixture"]) { response = $0 }
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
        try session.send(.verify, fields: ["itemId": "invalid-id"]) { denied = $0 }
        try await waitFor("validation response") { denied != nil }
        guard denied?.ok == false else { throw Failure.unexpected("Invalid item was accepted") }

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
        try session.send(.library, fields: [:]) { _ in receivedMalformedReply = true }
        try await waitFor("malformed response handling") { exited }
        guard !receivedMalformedReply, exitError == "The worker returned an invalid response." else {
            throw Failure.unexpected("Malformed response did not fail the session")
        }

        print("Worker session passed capture, search, denied request, restart and malformed response.")
    }

    private static func waitFor(_ event: String, condition: () -> Bool) async throws {
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
