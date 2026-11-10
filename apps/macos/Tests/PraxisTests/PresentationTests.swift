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
        let worker = try #require(Bundle.main.resourceURL?
            .appendingPathComponent("worker/Praxis.Worker.dll").path)
        #expect(FileManager.default.fileExists(atPath: worker))
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
