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

    @Test func verificationUsesEnabledCheckingProviderButNeverInventsARemoteDefault() {
        func provider(_ id: String, enabled: Bool = true) -> VerificationProvider {
            VerificationProvider(providerId: id, executable: nil, endpoint: nil, model: nil,
                                 revision: 1, costWeight: 1, dailyLimit: 0, enabled: enabled, effort: nil)
        }
        let connected = [provider(VerificationProvider.ollama),
                         provider(VerificationProvider.codex),
                         provider(VerificationProvider.claudeCode, enabled: false)]
        #expect(VerificationProviderDefault.choice(checkingProvider: VerificationProvider.codex, providers: connected) == VerificationProvider.codex)
        #expect(VerificationProviderDefault.choice(checkingProvider: VerificationProvider.claudeCode, providers: connected).isEmpty)
        #expect(VerificationProviderDefault.choice(checkingProvider: nil, providers: connected).isEmpty)
        #expect(VerificationProviderDefault.choice(checkingProvider: VerificationProvider.ollama, providers: connected).isEmpty)
    }

    @MainActor
    @Test func verificationRetainsManualChoiceWhenCheckingSettingsRefresh() {
        let workbench = VerificationWorkbenchModel(channel: WorkerChannel(worker: WorkerSession()))
        let first = VerificationRequest(requestId: "one", reason: "manual", status: "awaiting_approval",
                                        providerId: nil, createdUtc: "", failureCode: nil, modelName: nil,
                                        inputTokens: nil, outputTokens: nil, payload: "{}", items: [])
        let second = VerificationRequest(requestId: "two", reason: "manual", status: "awaiting_approval",
                                         providerId: nil, createdUtc: "", failureCode: nil, modelName: nil,
                                         inputTokens: nil, outputTokens: nil, payload: "{}", items: [])
        workbench.setPreferredProvider(VerificationProvider.codex)
        workbench.select(first)
        #expect(workbench.providerChoice == VerificationProvider.codex)
        #expect(!workbench.remoteConsent)

        workbench.chooseProvider("")
        workbench.setPreferredProvider(VerificationProvider.claudeCode)
        #expect(workbench.providerChoice.isEmpty)
        workbench.select(second)
        #expect(workbench.providerChoice == VerificationProvider.claudeCode)

        workbench.setPreferredProvider(VerificationProvider.codex)
        #expect(workbench.providerChoice == VerificationProvider.codex)
        #expect(!workbench.remoteConsent)
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
