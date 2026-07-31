import Foundation

struct UsageService {
    private let usageURL = URL(string: "https://chatgpt.com/backend-api/wham/usage")!
    private let resetURL = URL(string: "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume")!

    func fetchRefreshingCredential(authData: Data) async throws -> AccountUsageRefresh {
        do {
            return AccountUsageRefresh(usage: try await fetch(authData: authData), authData: authData)
        } catch UsageServiceError.requestFailed(401) {
            let refreshedAuth = try await AuthTokenRefreshService().refresh(authData: authData)
            return AccountUsageRefresh(
                usage: try await fetch(authData: refreshedAuth),
                authData: refreshedAuth
            )
        }
    }

    func fetch(authData: Data) async throws -> UsageSnapshot {
        guard let root = try JSONSerialization.jsonObject(with: authData) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any],
              let accessToken = tokens["access_token"] as? String,
              let accountID = tokens["account_id"] as? String
        else {
            throw UsageServiceError.invalidAuth
        }

        var payload = try await fetchUsage(accessToken: accessToken, accountID: accountID)
        var automaticResetApplied = false
        if RateLimitResetPolicy.shouldConsume(
            usedPercent: payload.rateLimit.primaryWindow?.usedPercent,
            limitReached: payload.rateLimit.limitReached,
            allowed: payload.rateLimit.allowed,
            applicableResetCount: payload.resetCredits?.applicableAvailableCount ?? 0
        ) {
            let code = try await consumeReset(accessToken: accessToken, accountID: accountID)
            if code == "reset" || code == "already_redeemed" {
                automaticResetApplied = code == "reset"
                payload = try await fetchUsage(accessToken: accessToken, accountID: accountID)
            } else if code != "nothing_to_reset" && code != "no_credit" {
                throw UsageServiceError.resetRejected(code)
            }
        }

        guard let primary = payload.rateLimit.primaryWindow else {
            throw UsageServiceError.invalidResponse
        }
        return UsageSnapshot(
            primary: map(primary),
            secondary: payload.rateLimit.secondaryWindow.map(map),
            creditsBalance: payload.credits?.balance,
            fetchedAt: Date().timeIntervalSinceReferenceDate,
            availableResetCount: payload.resetCredits?.availableCount,
            automaticResetApplied: automaticResetApplied
        )
    }

    private func fetchUsage(accessToken: String, accountID: String) async throws -> UsageResponse {
        var request = authorizedRequest(url: usageURL, accessToken: accessToken, accountID: accountID)
        request.timeoutInterval = 20
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw UsageServiceError.requestFailed((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        return try JSONDecoder().decode(UsageResponse.self, from: data)
    }

    private func consumeReset(accessToken: String, accountID: String) async throws -> String {
        var request = authorizedRequest(url: resetURL, accessToken: accessToken, accountID: accountID)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "redeem_request_id": UUID().uuidString
        ])
        request.timeoutInterval = 20
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw UsageServiceError.requestFailed((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        guard let body = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let code = body["code"] as? String
        else {
            throw UsageServiceError.invalidResponse
        }
        return code
    }

    private func authorizedRequest(url: URL, accessToken: String, accountID: String) -> URLRequest {
        var request = URLRequest(url: url)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(accountID, forHTTPHeaderField: "ChatGPT-Account-Id")
        return request
    }

    private func map(_ window: APIUsageWindow) -> UsageWindow {
        UsageWindow(
            usedPercent: window.usedPercent,
            resetAt: window.resetAt.map {
                Date(timeIntervalSince1970: $0).timeIntervalSinceReferenceDate
            },
            windowSeconds: window.limitWindowSeconds
        )
    }
}

struct AccountUsageRefresh: Sendable {
    let usage: UsageSnapshot
    let authData: Data
}

struct ProviderUsageService {
    private let claudibleUsageURL = URL(string: "https://claudible.io/dashboard/lookup")!

    func fetch(profile: ProviderProfile, apiKey: String) async throws -> UsageSnapshot {
        guard profile.providerID == "claudible" else {
            throw UsageServiceError.unsupportedProvider(profile.name)
        }

        var request = URLRequest(url: claudibleUsageURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["key": apiKey])
        request.timeoutInterval = 20

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw UsageServiceError.requestFailed((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        let payload = try JSONDecoder().decode(ClaudibleUsageResponse.self, from: data)
        guard payload.valid, let balance = payload.balance, let dailyQuota = payload.dailyQuota,
              dailyQuota > 0 else {
            throw UsageServiceError.invalidResponse
        }

        let remainingPercent = max(0, min(100, balance / dailyQuota * 100))
        var utcCalendar = Calendar(identifier: .gregorian)
        utcCalendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let nextUTCMidnight = utcCalendar.nextDate(
            after: Date(),
            matching: DateComponents(hour: 0, minute: 0, second: 0),
            matchingPolicy: .nextTime,
            direction: .forward
        )
        return UsageSnapshot(
            primary: UsageWindow(
                usedPercent: 100 - remainingPercent,
                resetAt: nextUTCMidnight?.timeIntervalSinceReferenceDate,
                windowSeconds: 86_400,
                customTitle: "Daily"
            ),
            secondary: nil,
            creditsBalance: String(format: "%.2f / %.0f credits", balance, dailyQuota),
            fetchedAt: Date().timeIntervalSinceReferenceDate,
            availableResetCount: nil,
            automaticResetApplied: false
        )
    }
}

private struct ClaudibleUsageResponse: Decodable {
    let valid: Bool
    let balance: Double?
    let dailyQuota: Double?
}

private struct UsageResponse: Decodable {
    let rateLimit: APIRateLimit
    let credits: APICredits?
    let resetCredits: APIRateLimitResetCredits?

    enum CodingKeys: String, CodingKey {
        case rateLimit = "rate_limit"
        case credits
        case resetCredits = "rate_limit_reset_credits"
    }
}

private struct APIRateLimit: Decodable {
    let allowed: Bool
    let limitReached: Bool
    let primaryWindow: APIUsageWindow?
    let secondaryWindow: APIUsageWindow?

    enum CodingKeys: String, CodingKey {
        case allowed
        case limitReached = "limit_reached"
        case primaryWindow = "primary_window"
        case secondaryWindow = "secondary_window"
    }
}

private struct APIUsageWindow: Decodable {
    let usedPercent: Double?
    let limitWindowSeconds: Double?
    let resetAt: Double?

    enum CodingKeys: String, CodingKey {
        case usedPercent = "used_percent"
        case limitWindowSeconds = "limit_window_seconds"
        case resetAt = "reset_at"
    }
}

private struct APICredits: Decodable {
    let balance: String?
}

private struct APIRateLimitResetCredits: Decodable {
    let availableCount: Int
    let applicableAvailableCount: Int

    enum CodingKeys: String, CodingKey {
        case availableCount = "available_count"
        case applicableAvailableCount = "applicable_available_count"
    }
}

enum UsageServiceError: LocalizedError {
    case invalidAuth
    case requestFailed(Int)
    case invalidResponse
    case unsupportedProvider(String)
    case resetRejected(String)

    var errorDescription: String? {
        switch self {
        case .invalidAuth: "This is not a ChatGPT Codex auth file."
        case .requestFailed(let status): "Usage request failed (HTTP \(status))."
        case .invalidResponse: "Codex returned an invalid usage response."
        case .unsupportedProvider(let provider): "Quota is not supported for \(provider) yet."
        case .resetRejected(let code): "Codex rejected the available quota reset (\(code))."
        }
    }
}
