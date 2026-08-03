import Foundation
import Security

enum KeychainStore {
    static let accountService = "com.openai.codex-account-bar.accounts"
    static let providerService = "com.openai.codex-account-bar.providers"
    private static let readQueue = DispatchQueue(label: "com.openai.codex-account-bar.keychain-read")

    static func read(service: String, account: String) async throws -> Data? {
        try await withCheckedThrowingContinuation { continuation in
            let resolver = KeychainReadResolver(continuation)
            readQueue.async {
                resolver.resolve(Result {
                    try blockingRead(service: service, account: account)
                })
            }
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 5) {
                resolver.resolve(.failure(KeychainReadError.timedOut))
            }
        }
    }

    private static func blockingRead(service: String, account: String) throws -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError(status) }
        return result as? Data
    }

    static func write(_ data: Data, service: String, account: String) throws {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        let update = SecItemUpdate(
            base as CFDictionary,
            [kSecValueData as String: data] as CFDictionary
        )
        if update == errSecItemNotFound {
            var insert = base
            insert[kSecValueData as String] = data
            insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let status = SecItemAdd(insert as CFDictionary, nil)
            guard status == errSecSuccess else { throw KeychainError(status) }
        } else if update != errSecSuccess {
            throw KeychainError(update)
        }
    }

    static func delete(service: String, account: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError(status)
        }
    }
}

private final class KeychainReadResolver: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Data?, Error>?

    init(_ continuation: CheckedContinuation<Data?, Error>) {
        self.continuation = continuation
    }

    func resolve(_ result: Result<Data?, Error>) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume(with: result)
    }
}

enum KeychainReadError: LocalizedError {
    case timedOut

    var errorDescription: String? {
        "Keychain did not respond within 5 seconds. Try locking and unlocking your Mac, then refresh again."
    }
}

struct KeychainError: LocalizedError {
    let status: OSStatus
    init(_ status: OSStatus) { self.status = status }

    var errorDescription: String? {
        SecCopyErrorMessageString(status, nil) as String? ?? "Keychain error \(status)"
    }
}
