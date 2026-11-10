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
