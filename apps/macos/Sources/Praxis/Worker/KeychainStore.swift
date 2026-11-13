import Foundation
import Security

/// Stores provider API keys as generic passwords (service "Praxis") in the owner's login Keychain,
/// readable only by Praxis. The app hands a key to its worker over the private pipe when the
/// worker starts or the key changes; the worker holds it in memory only, so keys never reach the
/// catalog, disk or a command line.
struct KeychainStore: Sendable {
    static let service = "Praxis"

    enum KeychainError: LocalizedError {
        case invalidInput
        case status(OSStatus)

        var errorDescription: String? {
            switch self {
            case .invalidInput: "The account name or key is invalid."
            case .status(let status): SecCopyErrorMessageString(status, nil) as String? ?? "Keychain error \(status)"
            }
        }
    }

    func contains(_ account: String) -> Bool {
        guard Self.valid(account) else { return false }
        var query = Self.query(account)
        query[kSecReturnAttributes as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    func read(_ account: String) -> String? {
        guard Self.valid(account) else { return nil }
        var query = Self.query(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    func save(_ secret: String, account: String) throws {
        guard Self.valid(account), !secret.isEmpty, secret.count <= 4_096 else { throw KeychainError.invalidInput }
        let value = Data(secret.utf8)
        let update = SecItemUpdate(Self.query(account) as CFDictionary, [kSecValueData as String: value] as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw KeychainError.status(update) }
        var item = Self.query(account)
        item[kSecAttrLabel as String] = "Praxis API key (\(account))"
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        item[kSecValueData as String] = value
        let status = SecItemAdd(item as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError.status(status) }
    }

    func delete(account: String) {
        guard Self.valid(account) else { return }
        SecItemDelete(Self.query(account) as CFDictionary)
    }

    static func valid(_ account: String) -> Bool {
        (1...128).contains(account.count) && account.allSatisfy { $0.isLetter || $0.isNumber || "-_.@".contains($0) }
    }

    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }
}
