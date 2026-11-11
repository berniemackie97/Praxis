import Foundation

struct WorkerLaunch {
    let executable: URL
    let arguments: [String]
    let inbox: URL
    let watchedRoots: [URL]

    static func prepare(
        dataRoot: String, inbox: String, additionalInboxes: String,
        rootApprovals: String, initialize: Bool, dotnetExecutable: String
    ) throws -> WorkerLaunch {
        let data = CanonicalPath.of(dataRoot)
        let additional = try WatchedFolderSettings.folders(additionalInboxes)
        let approvals = try WatchedFolderSettings.approvals(rootApprovals)
        let roots = ([inbox] + additional).map(CanonicalPath.of)
        guard roots.count >= 1 && roots.count <= 4 else { throw LaunchError.invalidRoots }
        for (index, root) in roots.enumerated() {
            guard !data.pathComponents.starts(with: root.pathComponents),
                  !root.pathComponents.starts(with: data.pathComponents),
                  !roots[..<index].contains(where: {
                      root.pathComponents.starts(with: $0.pathComponents) ||
                      $0.pathComponents.starts(with: root.pathComponents)
                  }) else {
                throw LaunchError.overlappingPaths
            }

            guard approvals[root.path] != nil else { throw LaunchError.approvalRequired }
            var directory: ObjCBool = false
            if FileManager.default.fileExists(atPath: root.path, isDirectory: &directory) {
                guard directory.boolValue,
                      let identity = WatchedFolderSettings.identity(root),
                      approvals[root.path] == identity else {
                    throw LaunchError.approvalRequired
                }
            }
        }

        let files = FileManager.default
        if initialize {
            guard roots.allSatisfy({ files.fileExists(atPath: $0.path) }) else {
                throw LaunchError.missingInbox
            }
            if !files.fileExists(atPath: data.path) {
                try files.createDirectory(at: data, withIntermediateDirectories: false,
                                          attributes: [.posixPermissions: 0o700])
            }
        }
        guard files.fileExists(atPath: data.path) else { throw LaunchError.missingCatalog }
        guard let worker = Bundle.main.resourceURL?.appendingPathComponent("worker/Praxis.Worker.dll"),
              files.fileExists(atPath: worker.path) else { throw LaunchError.missingWorker }

        let candidates = [
            dotnetExecutable.isEmpty ? nil : dotnetExecutable,
            ProcessInfo.processInfo.environment["PRAXIS_DOTNET_EXECUTABLE"],
            ProcessInfo.processInfo.environment["DOTNET_ROOT"].map { $0 + "/dotnet" },
            files.homeDirectoryForCurrentUser.appendingPathComponent(".dotnet/dotnet").path,
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/opt/homebrew/opt/dotnet/libexec/dotnet",
            "/usr/local/bin/dotnet"
        ].compactMap { $0 }
        guard let executable = candidates.first(where: files.isExecutableFile(atPath:)) else {
            throw LaunchError.missingRuntime
        }

        return WorkerLaunch(
            executable: URL(fileURLWithPath: executable),
            arguments: [worker.path, "--data-root", data.path] +
                roots.flatMap { ["--intake", $0.path] } + ["--stdio"],
            inbox: roots[0],
            watchedRoots: roots
        )
    }

    enum LaunchError: LocalizedError, Equatable {
        case overlappingPaths, invalidRoots, approvalRequired
        case missingInbox, missingCatalog, missingWorker, missingRuntime

        var errorDescription: String? {
            switch self {
            case .overlappingPaths: "Catalog and watched folders must not overlap."
            case .invalidRoots: "Select between one and four watched folders."
            case .approvalRequired: "A watched folder identity changed or has not been approved. Re-select the folder before starting."
            case .missingInbox: "Choose an available Inbox for first-time setup."
            case .missingCatalog: "The configured catalog is missing. Reconnect or restore it."
            case .missingWorker: "Praxis.Worker.dll is missing from the app bundle."
            case .missingRuntime: "Install the .NET 10 runtime and reopen Praxis."
            }
        }
    }
}
