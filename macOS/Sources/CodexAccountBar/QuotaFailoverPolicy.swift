import Foundation

enum QuotaFailoverPolicy {
    static let minimumCandidateRemaining = 5.0

    static func isExhausted(_ usage: UsageSnapshot?) -> Bool {
        guard let remaining = effectiveRemaining(usage) else { return false }
        return remaining <= 0.5
    }

    static func bestCandidate(
        from accounts: [SavedAccount],
        excludingAccountID: String
    ) -> SavedAccount? {
        accounts
            .filter { $0.id != excludingAccountID }
            .compactMap { account -> (SavedAccount, Double)? in
                guard let remaining = effectiveRemaining(account.usage),
                      remaining > minimumCandidateRemaining
                else { return nil }
                return (account, remaining)
            }
            .max { left, right in
                if left.1 == right.1 { return left.0.email > right.0.email }
                return left.1 < right.1
            }?
            .0
    }

    static func effectiveRemaining(_ usage: UsageSnapshot?) -> Double? {
        guard let usage else { return nil }
        let windows = [usage.primary, usage.secondary].compactMap { $0 }
        guard !windows.isEmpty else { return nil }
        return windows.map(\.remainingPercent).min()
    }
}
