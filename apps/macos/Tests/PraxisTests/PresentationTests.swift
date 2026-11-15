import Foundation
import Testing
@testable import Praxis

@Suite("Labels shown to the owner")
struct PresentationTests {
    @Test(arguments: [
        ("model_unauthenticated", "sign in to the provider CLI, then re-save it under Models"),
        ("model_quota_exhausted", "plan usage limit reached"),
        ("something_new", "something_new")
    ])
    func failureCodesExplainWhatToDo(code: String, label: String) {
        #expect(FailureCode.label(code) == label)
    }

    @Test func xcodeLaunchUsesAnApplicationBundleWithItsWorker() throws {
        #expect(Bundle.main.bundleURL.pathExtension == "app")
        #expect(Bundle.main.bundleIdentifier == "dev.praxis.renames-preview")
        #expect(Bundle.main.executableURL?.lastPathComponent == "Praxis")
        #expect(Bundle.main.infoDictionary?["LSMinimumSystemVersion"] as? String == "13.0")
        let worker = try #require(Bundle.main.resourceURL?
            .appendingPathComponent("worker/Praxis.Worker.dll").path)
        #expect(FileManager.default.fileExists(atPath: worker))
    }


    @Test func processingBacklogDecodesFromDesktopReply() throws {
        let response = """
            {"requestId":"intake","ok":true,
             "data":{"backlog":{"queued":2,"running":1,"failed":3}}}
            """
        let decoded = try JSONDecoder().decode(WorkerResponse.self, from: Data(response.utf8))
        let backlog = try #require(decoded.data?.backlog)
        #expect(backlog.queued == 2)
        #expect(backlog.running == 1)
        #expect(backlog.failed == 3)
    }


    @Test func intakeIncidentsDecodeWithResolvedAndOpenStates() throws {
        let response = """
            {"requestId":"intake","ok":true,
             "data":{"incidents":[
               {"incidentId":2,"rootPath":"/private/tmp/inbox","state":"reapproval_required",
                "startedUtc":"2026-11-11T01:00:00Z","resolvedUtc":null},
               {"incidentId":1,"rootPath":"/private/tmp/inbox","state":"offline",
                "startedUtc":"2026-11-11T00:00:00Z","resolvedUtc":"2026-11-11T01:00:00Z"}]}}
            """
        let decoded = try JSONDecoder().decode(WorkerResponse.self, from: Data(response.utf8))
        let incidents = try #require(decoded.data?.incidents)
        #expect(incidents.count == 2)
        #expect(incidents[0].state == "reapproval_required")
        #expect(incidents[0].resolvedUtc == nil)
        #expect(incidents[1].resolvedUtc != nil)
    }

    @Test func verifiedOriginalExportReceiptDecodesFromDesktopReply() throws {
        let response = """
            {"requestId":"export","ok":true,
             "data":{"exported":{"itemId":"source-item","revision":2,
                                  "sha256":"dd80536ce4e3c903e7d292afaeb035b70ac3e08a64c9101f195a3ebff3c76456","length":5}}}
            """
        let decoded = try JSONDecoder().decode(WorkerResponse.self, from: Data(response.utf8))
        let receipt = try #require(decoded.data?.exported)
        #expect(receipt.itemId == "source-item")
        #expect(receipt.revision == 2)
        #expect(receipt.sha256 == "dd80536ce4e3c903e7d292afaeb035b70ac3e08a64c9101f195a3ebff3c76456")
        #expect(receipt.length == 5)
    }

    @Test func countsUseSingularAndPlural() {
        #expect(Count.label(1, "source") == "1 source")
        #expect(Count.label(3, "mention") == "3 mentions")
    }

    @Test func vocabularyLabelsAreReadable() {
        #expect(KnowledgeVocabulary.label("part_of") == "part of")
        #expect(KnowledgeVocabulary.label(nil) == "untyped")
        #expect(TypeOrigin.label("owner") == "set by you")
        #expect(VerificationProvider.displayName(VerificationProvider.codex) == "Codex (ChatGPT plan)")
    }

    @Test func entityProposalsAreTitledWithTheirType() throws {
        let json = """
            {"proposalId":"p","sourceId":"s","itemId":"i","revision":1,"sourceSha256":"d","producer":"semantic-extract-v2",
             "kind":"relation","entityType":null,"value":"parses","evidence":"C3Reader parses PHY","evidenceOffset":0,
             "status":"pending","correction":null,"relationSubject":"C3Reader","relationObject":"PHY","flags":"instruction_like"}
            """
        let proposal = try JSONDecoder().decode(TopicProposal.self, from: Data(json.utf8))
        #expect(proposal.title == "C3Reader parses PHY")
        #expect(proposal.fromModel)
        #expect(proposal.instructionLike)
    }
}

@Suite("Workbench presentation helpers")
struct WorkbenchPresentationTests {
    @Test("Search snippets drop the stored path even when matches are marked inside it")
    func snippetDropsPath() {
        let locator = "/Users/me/Inbox/Contractor invoice.txt"
        let snippet = "/Users/me/Inbox/Contractor ⟦invoice⟧.txt\nInvoice 2026-118   from  Ortega"
        #expect(SnippetText.plain(snippet, locator: locator) == "Invoice 2026-118 from Ortega")
    }

    @Test("Search snippet highlights become bold runs without markers")
    func snippetHighlights() {
        let text = SnippetText.clean("Book flights before the ⟦invoice⟧ deadline", locator: "/x.md")
        #expect(String(text.characters) == "Book flights before the invoice deadline")
        let bold = text.runs.filter { $0.inlinePresentationIntent == .stronglyEmphasized }
        #expect(bold.count == 1)
    }

    @Test("Fallback chains stay ordered and always end in a terminal step")
    func fallbackChainTerminal() {
        #expect(FallbackChain.toggled(["local_if_verified", "hold", "index_only"], step: "index_only", enabled: false)
            == ["local_if_verified", "hold", "index_only"])
        #expect(FallbackChain.toggled(["hold", "index_only"], step: "skip", enabled: true) == ["hold", "index_only", "skip"])
    }

    @Test("Go menu shortcuts number the workspaces in sidebar order")
    func sectionShortcuts() {
        #expect(AppSection.chat.shortcut.character == "1")
        #expect(AppSection.skills.shortcut.character == "0")
        #expect(Set(AppSection.allCases.map(\.shortcut.character)).count == AppSection.allCases.count)
        #expect(Set(AppSection.groups.flatMap(\.sections)) == Set(AppSection.allCases))
    }
}
