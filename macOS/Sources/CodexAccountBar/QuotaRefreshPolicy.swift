import Foundation

struct AccountCredentialCommit: Equatable {
    let savedAuth: Data
    let activeAuth: Data?
}

enum AccountCredentialResolver {
    static func resolve(accountID: String, savedAuth: Data, activeAuth: Data?) -> Data {
        guard let activeAuth, authAccountID(activeAuth) == accountID else {
            return savedAuth
        }
        return activeAuth
    }

    static func authAccountID(_ authData: Data) -> String? {
        guard let root = try? JSONSerialization.jsonObject(with: authData) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any]
        else {
            return nil
        }
        return tokens["account_id"] as? String
    }

    static func refreshCommit(
        accountID: String,
        sourceAuth: Data,
        refreshedAuth: Data,
        latestActiveAuth: Data?
    ) -> AccountCredentialCommit {
        guard let latestActiveAuth,
              authAccountID(latestActiveAuth) == accountID
        else {
            return AccountCredentialCommit(savedAuth: refreshedAuth, activeAuth: nil)
        }

        guard latestActiveAuth == sourceAuth else {
            return AccountCredentialCommit(savedAuth: latestActiveAuth, activeAuth: nil)
        }
        return AccountCredentialCommit(savedAuth: refreshedAuth, activeAuth: refreshedAuth)
    }
}

enum AccountAuthenticationFailure {
    static func isCredentialFailure(_ error: Error) -> Bool {
        if case AuthTokenRefreshError.requestFailed(let status) = error {
            return status == 401
        }
        if case UsageServiceError.requestFailed(let status) = error {
            return status == 401
        }
        return false
    }
}

enum RateLimitResetPolicy {
    static func shouldConsume(
        usedPercent: Double?,
        limitReached: Bool,
        allowed: Bool,
        applicableResetCount: Int
    ) -> Bool {
        guard applicableResetCount > 0 else { return false }
        return limitReached || !allowed || (usedPercent ?? 0) >= 100
    }
}
