import Foundation

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
