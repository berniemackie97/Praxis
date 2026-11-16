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

/// Bind approval to a directory on a persistent volume, not to the kernel's temporary
/// mount number. A replacement at the same path still has to be approved.
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
        signatures(url)?.current
    }

    static func matches(_ approval: String?, folder url: URL) -> Bool {
        guard let approval, let signatures = signatures(url) else { return false }
        return approval == signatures.current || approval == signatures.legacy
    }

    private static func signatures(_ url: URL) -> (current: String, legacy: String)? {
        guard CanonicalPath.of(url.path).path == url.path else { return nil }
        var before = stat()
        guard lstat(url.path, &before) == 0,
              before.st_mode & S_IFMT == S_IFDIR, before.st_ino != 0 else { return nil }

        let legacy = "\(UInt32(bitPattern: before.st_dev)):\(before.st_ino)"
        guard let uuid = (try? url.resourceValues(forKeys: [.volumeUUIDStringKey]))?.volumeUUIDString,
              let volume = UUID(uuidString: uuid), before.st_birthtimespec.tv_sec > 0 else {
            // Filesystems without a persistent volume or creation identity retain the stricter
            // mount-specific approval. Do not turn a legacy match into a persistent grant.
            return (legacy, legacy)
        }

        var after = stat()
        guard lstat(url.path, &after) == 0, before.st_dev == after.st_dev,
              before.st_ino == after.st_ino, before.st_mode & S_IFMT == after.st_mode & S_IFMT,
              before.st_birthtimespec.tv_sec == after.st_birthtimespec.tv_sec,
              before.st_birthtimespec.tv_nsec == after.st_birthtimespec.tv_nsec else { return nil }

        let birth = "\(before.st_birthtimespec.tv_sec):\(before.st_birthtimespec.tv_nsec)"
        return ("volume:\(volume.uuidString):\(before.st_ino):\(birth)", legacy)
    }
}
