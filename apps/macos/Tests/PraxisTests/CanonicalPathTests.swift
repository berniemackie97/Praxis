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

    @Test func ownerFolderSettingsRoundTripAndRejectCorruptPreferences() throws {
        let folders = ["/private/tmp/a", "/private/tmp/b"]
        #expect(try WatchedFolderSettings.folders(WatchedFolderSettings.encode(folders)) == folders)
        let approvals = ["/private/tmp/a": "12:34"]
        #expect(try WatchedFolderSettings.approvals(WatchedFolderSettings.encode(approvals)) == approvals)
        #expect(throws: DecodingError.self) {
            try WatchedFolderSettings.folders("not-json")
        }
    }

    @Test func folderIdentityIsStableForOneApprovedLocalDirectory() throws {
        let selected = FileManager.default.temporaryDirectory
            .appendingPathComponent("praxis-approval-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: selected, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: selected) }
        let canonical = CanonicalPath.of(selected.path)
        let identity = try #require(WatchedFolderSettings.identity(canonical))
        #expect(identity == WatchedFolderSettings.identity(canonical))
    }

    @Test func unchangedPathStillNeedsOwnerApprovalAndNestedRootsAreDenied() throws {
        let parent = FileManager.default.temporaryDirectory
            .appendingPathComponent("praxis-launch-\(UUID().uuidString)")
        let source = parent.appendingPathComponent("inbox")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: parent) }

        let sourcePath = CanonicalPath.of(source.path).path
        let metadata = try #require(WatchedFolderSettings.identity(CanonicalPath.of(source.path)))
        let good = WatchedFolderSettings.encode([sourcePath: metadata])

        do {
            _ = try WorkerLaunch.prepare(dataRoot: parent.appendingPathComponent("catalog").path,
                inbox: sourcePath, additionalInboxes: "[]",
                rootApprovals: "{}", initialize: false, dotnetExecutable: "")
            Issue.record("A folder without an owner approval was accepted.")
        } catch { }

        do {
            _ = try WorkerLaunch.prepare(dataRoot: parent.appendingPathComponent("catalog").path,
                inbox: sourcePath, additionalInboxes: "[]",
                rootApprovals: WatchedFolderSettings.encode([sourcePath: "untrusted"]),
                initialize: false, dotnetExecutable: "")
            Issue.record("A folder with a changed identity was accepted.")
        } catch { }

        do {
            _ = try WorkerLaunch.prepare(dataRoot: parent.appendingPathComponent("catalog").path,
                inbox: sourcePath, additionalInboxes: WatchedFolderSettings.encode([sourcePath]),
                rootApprovals: good, initialize: false, dotnetExecutable: "")
            Issue.record("Overlapping watched folders were accepted.")
        } catch { }
    }
}
