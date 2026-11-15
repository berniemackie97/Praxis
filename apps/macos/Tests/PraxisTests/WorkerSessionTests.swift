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

        var atlas: WorkerReply<GraphPage>?
        try session.send("graphAtlas", fields: ["limit": 100], as: GraphPage.self) { atlas = $0 }
        try await waitFor("global graph atlas") { atlas != nil }
        guard atlas?.ok == true, atlas?.data?.outcome == "empty",
              atlas?.data?.nodes.isEmpty == true, atlas?.data?.edges.isEmpty == true else {
            throw Failure.unexpected("Empty global atlas exposed unrelated data")
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

    @Test("Chat keeps drafts, errors, replies and cancellation with the originating conversation")
    func chatIsolation() async throws {
        let executable = URL(fileURLWithPath: try #require(WorkerFixture.dotnetPath))
        let worker = try #require(WorkerFixture.workerPath)
        let fixture = FileManager.default.temporaryDirectory
            .appendingPathComponent("praxis-chat-\(UUID().uuidString)")
        let catalog = fixture.appendingPathComponent("Catalog")
        let inbox = fixture.appendingPathComponent("Inbox")
        for folder in [fixture, catalog, inbox] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false,
                                                    attributes: [.posixPermissions: 0o700])
        }
        defer { try? FileManager.default.removeItem(at: fixture) }

        // A real child process with deterministic CLI output; no provider sign-in or network.
        let fake = fixture.appendingPathComponent("codex-fixture")
        try """
        #!/bin/sh
        json=no
        while [ "$#" -gt 0 ]; do
          case "$1" in
            --json) json=yes ;;
            -m) shift; model="$1" ;;
            -o) shift; answer="$1" ;;
            model_reasoning_effort=*) effort="$1" ;;
          esac
          shift
        done
        [ "$json" = yes ] || exit 5
        input=$(cat)
        latest=$(printf '%s\\n' "$input" | grep '^Owner: ' | tail -1)
        case "$latest" in
          *"BLOCK A"*) sleep 60 ;;
          *"FAIL B"*)
            printf '%s\\n' '{"type":"turn.failed","error":{"message":"tool requires approval: fixture-private"}}'
            exit 1 ;;
          *)
            printf 'PING %s %s\\n' "$model" "$effort" > "$answer"
            printf '%s\\n' '{"type":"turn.completed","usage":{"input_tokens":123,"cached_input_tokens":23,"output_tokens":7}}' ;;
        esac
        """.write(to: fake, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: fake.path)

        let session = WorkerSession()
        let model = ChatWorkbenchModel(channel: WorkerChannel(worker: session))
        var exited = false
        try session.start(executable: executable,
                          arguments: [worker, "--data-root", catalog.path, "--intake", inbox.path, "--stdio"],
                          onDiagnostic: { _ in }, onExit: { _ in model.workerExited(); exited = true })
        defer { session.stop() }

        var connected = false
        try session.send("agentBackendSave", fields: [
            "backendId": "fixture-codex", "kind": "codex", "model": "fixture-v1",
            "executable": fake.path, "enabled": true, "effort": "low"
        ], as: ChangedFlag.self) { connected = $0.data?.changed == true }
        try await waitFor("saved fixture connection") { connected }
        model.refresh()
        try await waitFor("loaded fixture connection") { model.backendChoice == "fixture-codex" }

        model.draft = "BLOCK A"
        model.send()
        model.send() // A second click during the conversation-create handshake must do nothing.
        try await waitFor("A running") { model.running }
        let first = try #require(model.selected)
        model.draft = "A unsent follow-up"
        model.newConversation()
        #expect(model.draft.isEmpty)
        #expect(!model.running)

        model.draft = "FAIL B"
        model.send()
        model.select(first)
        #expect(model.draft == "A unsent follow-up")
        try await waitFor("B created") { model.conversations.count == 2 }
        let second = try #require(model.conversations.first { $0.id != first.id })
        try await waitFor("B ended") { !model.hasWork(second.id) }
        #expect(model.running)
        #expect(model.error == nil)
        model.select(second)
        #expect(model.error == ChatFailureText.describe("model_approval_required"))
        #expect(model.draft.isEmpty)

        model.chooseModel("fixture-v2", defaultEffort: nil)
        try await waitFor("B model saved") { model.selected?.model == "fixture-v2" && !model.busy }
        model.chooseEffort("high")
        try await waitFor("B effort saved") { model.selected?.effort == "high" && !model.busy }
        #expect(model.selected?.backendId == first.backendId)
        // Reload from the worker and confirm the same conversation retains its choices.
        model.refresh()
        try await waitFor("B transcript reloaded") { model.messages.contains { $0.content == "FAIL B" } }
        #expect(model.activeModel == "fixture-v2")
        #expect(model.activeEffort == "high")

        model.newConversation()
        #expect(model.error == nil)
        model.draft = "ANSWER C"
        model.send()
        try await waitFor("C replied while A still runs") { model.latestReply?.text.hasPrefix("PING fixture-v1") == true }
        #expect(model.latestReply?.inputTokens == 123)
        #expect(model.latestReply?.outputTokens == 7)

        model.select(first)
        #expect(model.running)
        #expect(model.draft == "A unsent follow-up")
        #expect(model.latestReply == nil)
        model.cancel()
        try await waitFor("A canceled") { !model.running }
        #expect(model.notice == "Stopped.")
        model.select(second)
        #expect(model.notice == nil)
        #expect(model.selected?.id == second.id)
        #expect(model.activeModel == "fixture-v2")
        model.draft = "VERIFY B CHOICE"
        // B's history includes FAIL B, so the fixture treats only the last owner request as the trigger.
        model.send()
        try await waitFor("B dispatched with edited model and effort") {
            model.latestReply?.text == #"PING fixture-v2 model_reasoning_effort="high""#
        }
        session.stop()
        try await waitFor("fixture worker exited") { exited }
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
