import Foundation
import Testing
@testable import Praxis

/// The approval screen must show exactly what the worker will send.
@Suite("Verification payload review")
struct PayloadReviewTests {
    private let payload = """
        {"items":[
          {"ordinal":1,"kind":"entity","name":"Alex Rivera","statedType":"person",
           "quote":"with Alex Rivera","context":"Discussed the roadmap with Alex Rivera.","source":"m.md"},
          {"ordinal":2,"kind":"relation","subject":"C3Reader","predicate":"part_of","object":"OpenConquerClient",
           "quote":"C3Reader in OpenConquerClient","context":"The C3Reader in OpenConquerClient.","source":"c.md"},
          {"ordinal":3,"kind":"entity_type","name":"OpenConquerClient","observedTypes":["organization","project"],
           "contexts":[{"observedType":"organization","quote":"OpenConquerClient parses","context":"alex rivera wrote it","source":"a.md"},
                       {"observedType":"project","quote":"OpenConquerClient draws","context":"draws terrain","source":"b.md"}]}
        ]}
        """

    @MainActor
    @Test func remoteDestinationAndConsentNeverCarryToAnotherRequest() throws {
        let workbench = VerificationWorkbenchModel(channel: WorkerChannel(worker: WorkerSession()))
        func row(_ id: String) throws -> VerificationRequest {
            let json = """
                {"requestId":"\(id)","reason":"owner_request","status":"awaiting_approval","createdUtc":"",
                 "payload":"","items":[],"itemCount":4}
                """
            return try JSONDecoder().decode(VerificationRequest.self, from: Data(json.utf8))
        }
        workbench.select(try row("one"))
        #expect(workbench.providerChoice == VerificationProvider.ollama)
        workbench.chooseProvider(VerificationProvider.codex)
        workbench.remoteConsent = true
        workbench.select(try row("two"))
        #expect(workbench.providerChoice == VerificationProvider.ollama)
        #expect(!workbench.remoteConsent)
    }

    @MainActor
    @Test func editingEvidenceInvalidatesConsentAndQueueRowsNeedNoPayload() throws {
        let model = VerificationWorkbenchModel(channel: WorkerChannel(worker: WorkerSession()))
        model.remoteConsent = true
        model.toggle(1)
        #expect(!model.remoteConsent)
        model.remoteConsent = true
        model.addRedaction("name")
        #expect(!model.remoteConsent)
        let summary = """
            {"requestId":"one","reason":"owner_request","status":"awaiting_approval","createdUtc":"",
             "payload":"","items":[],"itemCount":4}
            """
        let row = try JSONDecoder().decode(VerificationRequest.self, from: Data(summary.utf8))
        #expect(row.displayedItemCount == 4)
    }

    @Test func excludedItemsAreRemovedAndOthersKept() throws {
        let narrowed = PayloadPreview.narrow(payload, excluded: [1], redactions: [])
        let ordinals = PayloadItem.parse(narrowed).map(\.ordinal)
        #expect(ordinals == [2, 3])
    }

    @Test func redactionReplacesEveryCaseInsensitiveOccurrenceInNestedValues() {
        let narrowed = PayloadPreview.narrow(payload, excluded: [], redactions: [" Alex Rivera "])
        #expect(!narrowed.localizedCaseInsensitiveContains("alex rivera"))
        #expect(narrowed.components(separatedBy: PayloadPreview.redacted).count - 1 == 4)
    }

    @Test func blankRedactionsChangeNothing() {
        #expect(PayloadPreview.redact("Alex Rivera", ["", "   "]) == "Alex Rivera")
    }

    @Test func itemsAreTitledByKindWithAllQuotes() {
        let items = PayloadItem.parse(payload)
        #expect(items.map(\.title) == ["Alex Rivera as person", "C3Reader part of OpenConquerClient", "Type of OpenConquerClient"])
        #expect(items[2].quotes.map(\.source) == ["a.md", "b.md"])
    }

    @Test func malformedPayloadsParseToNothingAndPreviewUnchanged() {
        #expect(PayloadItem.parse("not json").isEmpty)
        #expect(PayloadPreview.narrow("not json", excluded: [1], redactions: ["x"]) == "not json")
    }
}
