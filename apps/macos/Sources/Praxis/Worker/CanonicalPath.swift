import Darwin
import Foundation

/// Canonical spelling of a file-system path, matching the worker's source locators: symbolic
/// links in existing ancestors are resolved with realpath(3), which keeps `/private` on macOS
/// (unlike `URL.resolvingSymlinksInPath`, which strips it), and a missing remainder is kept.
enum CanonicalPath {
    static func of(_ path: String) -> URL {
        var existing = URL(fileURLWithPath: path).standardizedFileURL
        var remainder: [String] = []
        while true {
            if let resolved = realpath(existing.path, nil) {
                defer { free(resolved) }
                return remainder.reversed().reduce(URL(fileURLWithPath: String(cString: resolved))) {
                    $0.appendingPathComponent($1)
                }
            }
            let parent = existing.deletingLastPathComponent()
            guard parent.path != existing.path else { return URL(fileURLWithPath: path).standardizedFileURL }
            remainder.append(existing.lastPathComponent)
            existing = parent
        }
    }
}

/// Owner-selected folder identity captured by Foundation; changing device or inode requires
/// a new explicit choice, even when the filesystem path has been reused.
enum WatchedFolderSettings {
    static func folders(_ value: String) throws -> [String] {
        try JSONDecoder().decode([String].self, from: Data(value.utf8))
    }

    static func approvals(_ value: String) throws -> [String: String] {
        try JSONDecoder().decode([String: String].self, from: Data(value.utf8))
    }

    static func encode<T: Encodable>(_ value: T) -> String {
        String(decoding: (try? JSONEncoder().encode(value)) ?? Data(), as: UTF8.self)
    }

    static func identity(_ url: URL) -> String? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let device = attributes[.systemNumber] as? NSNumber,
              let inode = attributes[.systemFileNumber] as? NSNumber else {
            return nil
        }
        return "\(device.uint64Value):\(inode.uint64Value)"
    }
}
