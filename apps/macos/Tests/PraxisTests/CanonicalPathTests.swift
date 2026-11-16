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
        #expect(identity.hasPrefix("volume:"))
        let note = selected.appendingPathComponent("note.md")
        let sorted = selected.appendingPathComponent("Sorted")
        try Data("test note".utf8).write(to: note)
        try FileManager.default.createDirectory(at: sorted, withIntermediateDirectories: false)
        try FileManager.default.moveItem(at: note, to: sorted.appendingPathComponent("note.md"))
        #expect(WatchedFolderSettings.matches(identity, folder: canonical))
        let wrongVolume = "volume:\(UUID().uuidString):" + identity.split(separator: ":").dropFirst(2).joined(separator: ":")
        #expect(!WatchedFolderSettings.matches(wrongVolume, folder: canonical))
    }

    @Test func existingCatalogOpensWithPendingIntakeButSetupStillRequiresApproval() throws {
        let files = FileManager.default
        let parent = files.temporaryDirectory.appendingPathComponent("praxis-launch-\(UUID().uuidString)")
        let source = parent.appendingPathComponent("inbox")
        let catalog = parent.appendingPathComponent("catalog")
        let privateMode: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
        try files.createDirectory(at: parent, withIntermediateDirectories: false, attributes: privateMode)
        defer { try? files.removeItem(at: parent) }
        try files.createDirectory(at: source, withIntermediateDirectories: false, attributes: privateMode)
        try files.createDirectory(at: catalog, withIntermediateDirectories: false, attributes: privateMode)

        let sourcePath = CanonicalPath.of(source.path).path
        let identity = try #require(WatchedFolderSettings.identity(CanonicalPath.of(source.path)))
        let approvals = WatchedFolderSettings.encode([sourcePath: identity])

        func expectDenied(_ expected: WorkerLaunch.LaunchError, _ action: () throws -> Void) {
            do {
                try action()
                Issue.record("Launch unexpectedly succeeded instead of failing with \(expected).")
            } catch let error as WorkerLaunch.LaunchError {
                #expect(error == expected)
            } catch {
                Issue.record("Launch failed with \(error), not \(expected).")
            }
        }

        func prepare(_ saved: String, initialize: Bool = false) throws -> WorkerLaunch {
            try WorkerLaunch.prepare(dataRoot: catalog.path, inbox: sourcePath,
                additionalInboxes: "[]", rootApprovals: saved, initialize: initialize,
                dotnetExecutable: "/usr/bin/true")
        }

        expectDenied(.approvalRequired) { _ = try prepare("{}", initialize: true) }
        for saved in ["{}", "corrupt", WatchedFolderSettings.encode([sourcePath: "untrusted"])] {
            let pending = try prepare(saved)
            #expect(pending.arguments.suffix(3) == ["--intake-pending", sourcePath, "--stdio"])
        }
        expectDenied(.overlappingPaths) {
            _ = try WorkerLaunch.prepare(dataRoot: catalog.path, inbox: sourcePath,
                additionalInboxes: WatchedFolderSettings.encode([sourcePath]),
                rootApprovals: approvals, initialize: false, dotnetExecutable: "/usr/bin/true")
        }
        expectDenied(.overlappingPaths) {
            _ = try WorkerLaunch.prepare(dataRoot: source.appendingPathComponent("Catalog").path,
                inbox: sourcePath, additionalInboxes: "[]", rootApprovals: approvals,
                initialize: false, dotnetExecutable: "/usr/bin/true")
        }

        let launch = try WorkerLaunch.prepare(dataRoot: catalog.path, inbox: sourcePath,
            additionalInboxes: "[]", rootApprovals: approvals,
            initialize: false, dotnetExecutable: "/usr/bin/true")
        #expect(launch.executable.path == "/usr/bin/true")
        #expect(launch.arguments.suffix(3) == ["--intake", sourcePath, "--stdio"])

        let moved = parent.appendingPathComponent("moved")
        try files.moveItem(at: source, to: moved)
        try files.createDirectory(at: source, withIntermediateDirectories: false, attributes: privateMode)
        let pending = try prepare(approvals)
        #expect(pending.arguments.suffix(3) == ["--intake-pending", sourcePath, "--stdio"])

        try files.removeItem(at: source)
        try files.createSymbolicLink(at: source, withDestinationURL: catalog)
        let linked = try prepare(approvals)
        #expect(linked.arguments.suffix(3) == ["--intake-pending", sourcePath, "--stdio"])
    }
}
