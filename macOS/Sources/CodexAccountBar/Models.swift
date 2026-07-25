import Foundation

struct UsageWindow: Codable, Hashable {
    var usedPercent: Double?
    var resetAt: Double?
    var windowSeconds: Double?
}

struct UsageSnapshot: Codable, Hashable {
    var primary: UsageWindow?
    var secondary: UsageWindow?
    var creditsBalance: String?
    var fetchedAt: Double?
}

struct SavedAccount: Codable, Identifiable, Hashable {
    let id: String
    var email: String
    var plan: String
    var label: String?
    var usage: UsageSnapshot?
    var lastWarmupAt: Double?

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

    static func preset(_ preset: ProviderPreset) -> ProviderProfile {
        switch preset {
        case .claudible:
            ProviderProfile(
                name: "Claudible · Sonnet 5",
                providerID: "claudible",
                adapter: "anthropic",
                baseURL: "https://vip.claudible.io",
                defaultModel: "claude-sonnet-5",
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
