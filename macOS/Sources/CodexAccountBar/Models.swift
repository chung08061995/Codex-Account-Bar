import Foundation

struct UsageWindow: Codable, Hashable {
    var usedPercent: Double?
    var resetAt: Double?
    var windowSeconds: Double?
    var customTitle: String?

    var remainingPercent: Double {
        max(0, min(100, 100 - (usedPercent ?? 0)))
    }

    var title: String {
        if let customTitle, !customTitle.isEmpty { return customTitle }
        guard let seconds = windowSeconds else { return "Quota" }
        if seconds <= 6 * 60 * 60 { return "Session" }
        if seconds <= 8 * 24 * 60 * 60 { return "Weekly" }
        if seconds <= 35 * 24 * 60 * 60 { return "Monthly" }
        return "Quota"
    }

    var resetText: String {
        guard let resetAt else { return "Reset unknown" }
        let date = resetAt > 1_200_000_000
            ? Date(timeIntervalSince1970: resetAt)
            : Date(timeIntervalSinceReferenceDate: resetAt)
        let timestamp = date.formatted(date: .abbreviated, time: .shortened)
        let relative = date.formatted(.relative(presentation: .numeric))
        return "Resets \(timestamp) (\(relative))"
    }
}

struct UsageSnapshot: Codable, Hashable {
    var primary: UsageWindow?
    var secondary: UsageWindow?
    var creditsBalance: String?
    var fetchedAt: Double?
    var availableResetCount: Int? = nil
    var automaticResetApplied: Bool? = nil
}

enum AccountCredentialStatus: String, Codable, Hashable {
    case ready
    case signInRequired
}

struct SavedAccount: Codable, Identifiable, Hashable {
    let id: String
    var email: String
    var plan: String
    var label: String?
    var usage: UsageSnapshot?
    var lastWarmupAt: Double?
    var credentialStatus: AccountCredentialStatus? = nil

    var needsSignIn: Bool { credentialStatus == .signInRequired }

    var displayName: String {
        let trimmed = (label ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? email : trimmed
    }
}

enum ProviderPreset: String, CaseIterable, Identifiable {
    case claudible
    case gemini
    case kiro
    case custom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .claudible: "Claudible"
        case .gemini: "Google Gemini"
        case .kiro: "Kiro"
        case .custom: "Custom provider"
        }
    }
}

struct ProviderProfile: Codable, Identifiable, Hashable {
    var id: UUID
    var name: String
    var providerID: String
    var adapter: String
    var baseURL: String
    var defaultModel: String
    var registryProvider: Bool
    var authentication: String
    var createdAt: Date

    init(
        id: UUID = UUID(),
        name: String,
        providerID: String,
        adapter: String,
        baseURL: String,
        defaultModel: String,
        registryProvider: Bool,
        authentication: String,
        createdAt: Date = Date()
    ) {
        self.id = id
        self.name = name
        self.providerID = providerID
        self.adapter = adapter
        self.baseURL = baseURL
        self.defaultModel = defaultModel
        self.registryProvider = registryProvider
        self.authentication = authentication
        self.createdAt = createdAt
    }

    var requiresAPIKey: Bool { authentication == "key" }
    var requiresOAuth: Bool { authentication == "oauth" }
    var codexModelSlug: String {
        guard !defaultModel.contains("/") else { return defaultModel }
        return "\(providerID)/\(defaultModel)"
    }

    func migrated() -> ProviderProfile {
        guard providerID == "claudible",
              baseURL == "https://vip.claudible.io" || baseURL == "https://claudible.io/v1"
        else { return self }
        var copy = self
        copy.name = "Claudible · GPT 5.6 Sol"
        copy.adapter = "openai-responses"
        copy.baseURL = "https://claude.claudible.io/v1"
        copy.defaultModel = "gpt-5.6-sol"
        return copy
    }

    static func preset(_ preset: ProviderPreset) -> ProviderProfile {
        switch preset {
        case .claudible:
            ProviderProfile(
                name: "Claudible · GPT 5.6 Sol",
                providerID: "claudible",
                adapter: "openai-responses",
                baseURL: "https://claude.claudible.io/v1",
                defaultModel: "gpt-5.6-sol",
                registryProvider: false,
                authentication: "key"
            )
        case .gemini:
            ProviderProfile(
                name: "Google Gemini",
                providerID: "google",
                adapter: "google",
                baseURL: "https://generativelanguage.googleapis.com",
                defaultModel: "gemini-3-pro",
                registryProvider: true,
                authentication: "key"
            )
        case .kiro:
            ProviderProfile(
                name: "Kiro",
                providerID: "kiro",
                adapter: "kiro",
                baseURL: "https://runtime.us-east-1.kiro.dev",
                defaultModel: "kiro-auto",
                registryProvider: true,
                authentication: "oauth"
            )
        case .custom:
            ProviderProfile(
                name: "Custom provider",
                providerID: "custom",
                adapter: "openai-responses",
                baseURL: "https://example.com/v1",
                defaultModel: "",
                registryProvider: false,
                authentication: "key"
            )
        }
    }
}
