import Foundation
import Testing
@testable import Praxis

/// The launcher must spell paths the way the worker records source locators (ADR 0035).
@Suite("Canonical paths")
struct CanonicalPathTests {
    @Test func linkedAncestorsResolveAndKeepPrivate() {
        #expect(CanonicalPath.of("/tmp").path == "/private/tmp")
        #expect(CanonicalPath.of("/var/../tmp").path == "/private/tmp")
    }

    @Test func missingRemainderIsAppendedToTheResolvedAncestor() {
        let missing = "praxis-missing-\(UUID().uuidString)"
        #expect(CanonicalPath.of("/tmp/\(missing)/inbox").path == "/private/tmp/\(missing)/inbox")
    }

    @Test func pathsWithoutLinksAreUnchanged() {
        #expect(CanonicalPath.of("/Applications").path == "/Applications")
    }
}
