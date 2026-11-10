import Foundation

struct WorkerLaunch {
    let executable: URL
    let arguments: [String]
    let inbox: URL

    static func prepare(
        dataRoot: String, inbox: String, initialize: Bool, dotnetExecutable: String
    ) throws -> WorkerLaunch {
        let data = CanonicalPath.of(dataRoot)
        let source = CanonicalPath.of(inbox)
        guard !data.pathComponents.starts(with: source.pathComponents),
              !source.pathComponents.starts(with: data.pathComponents) else {
            throw LaunchError.overlappingPaths
        }

        let files = FileManager.default
        if initialize {
            guard files.fileExists(atPath: source.path) else { throw LaunchError.missingInbox }
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
            ProcessInfo.processInfo.environment["DOTNET_ROOT"].map { $0 + "/dotnet" },
            files.homeDirectoryForCurrentUser.appendingPathComponent(".dotnet/dotnet").path,
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/usr/local/bin/dotnet"
        ].compactMap { $0 }
        guard let executable = candidates.first(where: files.isExecutableFile(atPath:)) else {
            throw LaunchError.missingRuntime
        }

        return WorkerLaunch(
            executable: URL(fileURLWithPath: executable),
            arguments: [worker.path, "--data-root", data.path, "--intake", source.path, "--stdio"],
            inbox: source
        )
    }

    private enum LaunchError: LocalizedError {
        case overlappingPaths, missingInbox, missingCatalog, missingWorker, missingRuntime

        var errorDescription: String? {
            switch self {
            case .overlappingPaths: "Catalog and watched Inbox must not overlap."
            case .missingInbox: "Choose an available Inbox for first-time setup."
            case .missingCatalog: "The configured catalog is missing. Reconnect or restore it."
            case .missingWorker: "Praxis.Worker.dll is missing from the app bundle."
            case .missingRuntime: "Install the .NET 10 runtime and reopen Praxis."
            }
        }
    }
}
