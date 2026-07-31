import Foundation

struct AuthTokenRefreshService: Sendable {
    private let tokenURL = URL(string: "https://auth.openai.com/oauth/token")!
    private let clientID = "app_EMoamEEZ73f0CkXaXp7hrann"

    func refresh(authData: Data) async throws -> Data {
        guard let root = try JSONSerialization.jsonObject(with: authData) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any],
              let refreshToken = tokens["refresh_token"] as? String,
              !refreshToken.isEmpty
        else {
            throw AuthTokenRefreshError.missingRefreshToken
        }

        var components = URLComponents()
        components.queryItems = [
            URLQueryItem(name: "grant_type", value: "refresh_token"),
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "refresh_token", value: refreshToken)
        ]
        var request = URLRequest(url: tokenURL)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = components.percentEncodedQuery?.data(using: .utf8)
        request.timeoutInterval = 20

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw AuthTokenRefreshError.requestFailed((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        let refreshed = try JSONDecoder().decode(OAuthRefreshResponse.self, from: data)
        return try AuthDocumentUpdater.applyingRefresh(
            to: authData,
            accessToken: refreshed.accessToken,
            idToken: refreshed.idToken,
            refreshToken: refreshed.refreshToken,
            refreshedAt: Date()
        )
    }
}

enum AuthDocumentUpdater {
    static func applyingRefresh(
        to authData: Data,
        accessToken: String,
        idToken: String?,
        refreshToken: String?,
        refreshedAt: Date
    ) throws -> Data {
        guard var root = try JSONSerialization.jsonObject(with: authData) as? [String: Any],
              var tokens = root["tokens"] as? [String: Any]
        else {
            throw AuthTokenRefreshError.invalidAuth
        }
        tokens["access_token"] = accessToken
        if let idToken, !idToken.isEmpty { tokens["id_token"] = idToken }
        if let refreshToken, !refreshToken.isEmpty { tokens["refresh_token"] = refreshToken }
        root["tokens"] = tokens
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        root["last_refresh"] = formatter.string(from: refreshedAt)
        return try JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])
    }
}

private struct OAuthRefreshResponse: Decodable {
    let accessToken: String
    let idToken: String?
    let refreshToken: String?

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case idToken = "id_token"
        case refreshToken = "refresh_token"
    }
}

enum AuthTokenRefreshError: LocalizedError {
    case missingRefreshToken
    case invalidAuth
    case requestFailed(Int)

    var errorDescription: String? {
        switch self {
        case .missingRefreshToken: "This account must be switched once to renew its sign-in."
        case .invalidAuth: "The saved Codex auth document is invalid."
        case .requestFailed(let status): "Codex sign-in refresh failed (HTTP \(status))."
        }
    }
}
