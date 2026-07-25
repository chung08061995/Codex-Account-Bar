import Foundation
import SwiftUI

@MainActor
final class AccountStore: ObservableObject {
    @Published private(set) var accounts: [SavedAccount] = []
    @Published private(set) var providers: [ProviderProfile] = []
    @Published var activeAccountID: String?
    @Published var activeProviderID: UUID?
    @Published var statusText = "Ready"
    @Published var isBusy = false
    @Published var isRefreshingUsage = false
    @Published var errorMessage: String?

    private let accountMetadataKey = "savedAccountMetadata.v1"
    private let providerMetadataKey = "savedProviderMetadata.v1"
    private let activeProviderKey = "activeProviderID.v1"
    private let codexService = CodexService()
    private let providerService = ProviderService()
    private let usageService = UsageService()

    var activeQuotaWindow: UsageWindow? {
        guard activeProviderID == nil, let activeAccountID else { return nil }
        return accounts.first(where: { $0.id == activeAccountID })?.usage?.primary
    }

    init() {
        load()
        Task { await refreshUsage(showErrors: false) }
    }

    func load() {
        if let data = UserDefaults.standard.data(forKey: accountMetadataKey) {
            accounts = (try? JSONDecoder().decode([SavedAccount].self, from: data)) ?? []
        }
        if let data = UserDefaults.standard.data(forKey: providerMetadataKey) {
            providers = (try? JSONDecoder().decode([ProviderProfile].self, from: data)) ?? []
        }
        if let raw = UserDefaults.standard.string(forKey: activeProviderKey) {
            activeProviderID = UUID(uuidString: raw)
        }
        activeAccountID = codexService.activeAccountID(accounts: accounts)
    }

    func saveProvider(_ profile: ProviderProfile, apiKey: String) {
        do {
            if profile.requiresAPIKey, !apiKey.isEmpty {
                try KeychainStore.write(
                    Data(apiKey.utf8),
                    service: KeychainStore.providerService,
                    account: profile.id.uuidString
                )
            }
            if let index = providers.firstIndex(where: { $0.id == profile.id }) {
                providers[index] = profile
            } else {
                providers.append(profile)
            }
            persistProviders()
            statusText = "Saved \(profile.name)"
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func deleteProvider(_ profile: ProviderProfile) {
        providers.removeAll { $0.id == profile.id }
        try? KeychainStore.delete(
            service: KeychainStore.providerService,
            account: profile.id.uuidString
        )
        if activeProviderID == profile.id {
            activeProviderID = nil
            UserDefaults.standard.removeObject(forKey: activeProviderKey)
        }
        persistProviders()
    }

    func addAccount() {
        perform {
            self.statusText = "Complete sign-in in your browser…"
            let result = try await self.codexService.loginIsolated()
            try KeychainStore.write(
                result.authData,
                service: KeychainStore.accountService,
                account: result.account.id
            )
            if let index = self.accounts.firstIndex(where: { $0.id == result.account.id }) {
                self.accounts[index] = result.account
            } else {
                self.accounts.append(result.account)
            }
            self.persistAccounts()
            self.statusText = "Added \(result.account.displayName)"
        }
    }

    func refreshUsage(showErrors: Bool = true) async {
        guard !isRefreshingUsage else { return }
        isRefreshingUsage = true
        if !isBusy { statusText = "Refreshing quota…" }
        var failures: [String] = []
        let service = usageService

        await withTaskGroup(of: (String, Result<UsageSnapshot, Error>).self) { group in
            for account in accounts {
                group.addTask {
                    do {
                        guard let auth = try KeychainStore.read(
                            service: KeychainStore.accountService,
                            account: account.id
                        ) else {
                            throw AccountStoreError.missingCredentials(account.displayName)
                        }
                        return (account.id, .success(try await service.fetch(authData: auth)))
                    } catch {
                        return (account.id, .failure(error))
                    }
                }
            }

            for await (accountID, result) in group {
                guard let index = accounts.firstIndex(where: { $0.id == accountID }) else { continue }
                switch result {
                case .success(let usage):
                    accounts[index].usage = usage
                case .failure(let error):
                    failures.append(error.localizedDescription)
                }
            }
        }

        persistAccounts()
        isRefreshingUsage = false
        if !isBusy {
            statusText = failures.isEmpty ? "Quota refreshed" : "Quota refreshed with \(failures.count) error(s)"
        }
        if showErrors, failures.count == accounts.count, let first = failures.first {
            errorMessage = first
        }
    }

    func activateAccount(_ account: SavedAccount) {
        perform {
            self.statusText = "Switching to \(account.displayName)…"
            guard let auth = try KeychainStore.read(
                service: KeychainStore.accountService,
                account: account.id
            ) else {
                throw AccountStoreError.missingCredentials(account.displayName)
            }
            await self.providerService.activateNativeOpenAI { self.statusText = $0 }
            try self.codexService.activateAccount(authData: auth)
            self.activeProviderID = nil
            UserDefaults.standard.removeObject(forKey: self.activeProviderKey)
            self.activeAccountID = account.id
            self.statusText = "Restarting Codex…"
            try await self.codexService.restartCodex()
            self.statusText = "Using \(account.displayName)"
        }
    }

    func activateProvider(_ profile: ProviderProfile) {
        perform {
            try await self.providerService.activate(
                profile: profile,
                allProfiles: self.providers
            ) { self.statusText = $0 }
            self.activeProviderID = profile.id
            self.activeAccountID = nil
            UserDefaults.standard.set(profile.id.uuidString, forKey: self.activeProviderKey)
            self.statusText = "Restarting Codex…"
            try await self.codexService.restartCodex()
            self.statusText = "Using \(profile.name)"
        }
    }

    private func perform(_ operation: @escaping @MainActor () async throws -> Void) {
        guard !isBusy else { return }
        isBusy = true
        errorMessage = nil
        Task {
            defer { isBusy = false }
            do {
                try await operation()
            } catch {
                errorMessage = error.localizedDescription
                statusText = "Activation failed"
            }
        }
    }

    private func persistProviders() {
        if let data = try? JSONEncoder().encode(providers) {
            UserDefaults.standard.set(data, forKey: providerMetadataKey)
        }
    }

    private func persistAccounts() {
        if let data = try? JSONEncoder().encode(accounts) {
            UserDefaults.standard.set(data, forKey: accountMetadataKey)
        }
    }
}

enum AccountStoreError: LocalizedError {
    case missingCredentials(String)
    var errorDescription: String? {
        switch self {
        case .missingCredentials(let account): "Credentials are missing for \(account)."
        }
    }
}
