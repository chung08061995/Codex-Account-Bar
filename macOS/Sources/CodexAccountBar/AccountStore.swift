import Foundation
import SwiftUI

@MainActor
final class AccountStore: ObservableObject {
    @Published private(set) var accounts: [SavedAccount] = []
    @Published private(set) var providers: [ProviderProfile] = []
    @Published private(set) var providerUsage: [UUID: UsageSnapshot] = [:]
    @Published var activeAccountID: String?
    @Published var activeProviderID: UUID?
    @Published var statusText = "Ready"
    @Published var isBusy = false
    @Published var isAddingAccount = false
    @Published var isRefreshingUsage = false
    @Published var errorMessage: String?

    private let accountMetadataKey = "savedAccountMetadata.v1"
    private let providerMetadataKey = "savedProviderMetadata.v1"
    private let activeProviderKey = "activeProviderID.v1"
    private let codexService = CodexService()
    private let providerService = ProviderService()
    private let usageService = UsageService()
    private let providerUsageService = ProviderUsageService()
    private var addAccountTask: Task<Void, Never>?
    private var refreshLoopTask: Task<Void, Never>?

    private let automaticRefreshInterval: UInt64 = 10 * 60 * 1_000_000_000

    var activeQuotaWindow: UsageWindow? {
        if let activeProviderID {
            return providerUsage[activeProviderID]?.primary
        }
        guard let activeAccountID else { return nil }
        return accounts.first(where: { $0.id == activeAccountID })?.usage?.primary
    }

    init() {
        load()
        Task { await refreshUsage(showErrors: false) }
        refreshLoopTask = Task { [weak self] in
            var nextDelay = self?.automaticRefreshInterval ?? 0
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: nextDelay)
                } catch {
                    break
                }
                guard let self else { break }
                let succeeded = await self.refreshUsage(showErrors: false)
                if succeeded {
                    nextDelay = self.automaticRefreshInterval
                } else {
                    let retryDelay = nextDelay == self.automaticRefreshInterval
                        ? 2 * 60 * 1_000_000_000
                        : nextDelay * 2
                    nextDelay = min(retryDelay, 30 * 60 * 1_000_000_000)
                }
            }
        }
    }

    deinit {
        refreshLoopTask?.cancel()
    }

    func load() {
        if let data = UserDefaults.standard.data(forKey: accountMetadataKey) {
            accounts = (try? JSONDecoder().decode([SavedAccount].self, from: data)) ?? []
        }
        if let data = UserDefaults.standard.data(forKey: providerMetadataKey) {
            let decoded = (try? JSONDecoder().decode([ProviderProfile].self, from: data)) ?? []
            providers = decoded.map { $0.migrated() }
            if providers != decoded { persistProviders() }
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
            Task { await refreshUsage(showErrors: false) }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func deleteProvider(_ profile: ProviderProfile) {
        providers.removeAll { $0.id == profile.id }
        providerUsage.removeValue(forKey: profile.id)
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

    func deleteAccount(_ account: SavedAccount) {
        accounts.removeAll { $0.id == account.id }
        try? KeychainStore.delete(
            service: KeychainStore.accountService,
            account: account.id
        )
        if activeAccountID == account.id {
            activeAccountID = nil
        }
        persistAccounts()
        statusText = "Removed \(account.displayName)"
    }

    func addAccount() {
        guard !isBusy else { return }
        isBusy = true
        isAddingAccount = true
        errorMessage = nil
        statusText = "Complete sign-in in your browser…"
        addAccountTask = Task {
            defer {
                isBusy = false
                isAddingAccount = false
                addAccountTask = nil
            }
            do {
                let result = try await codexService.loginIsolated()
                try Task.checkCancellation()
                try KeychainStore.write(
                    result.authData,
                    service: KeychainStore.accountService,
                    account: result.account.id
                )
                if let index = accounts.firstIndex(where: { $0.id == result.account.id }) {
                    accounts[index] = result.account
                } else {
                    accounts.append(result.account)
                }
                persistAccounts()
                statusText = "Added \(result.account.displayName)"
            } catch is CancellationError {
                statusText = "Sign-in cancelled"
            } catch {
                errorMessage = error.localizedDescription
                statusText = "Sign-in failed"
            }
        }
    }

    func cancelAddAccount() {
        guard isAddingAccount else { return }
        statusText = "Cancelling sign-in…"
        codexService.cancelLogin()
        addAccountTask?.cancel()
    }

    func refreshUsage(showErrors: Bool = true) async -> Bool {
        guard !isRefreshingUsage else { return false }
        isRefreshingUsage = true
        if !isBusy { statusText = "Refreshing quota…" }
        var failures: [String] = []
        let service = usageService
        let externalService = providerUsageService

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

        let quotaProviders = providers.filter { $0.providerID == "claudible" && $0.requiresAPIKey }
        await withTaskGroup(of: (UUID, Result<UsageSnapshot, Error>).self) { group in
            for profile in quotaProviders {
                group.addTask {
                    do {
                        guard let data = try KeychainStore.read(
                            service: KeychainStore.providerService,
                            account: profile.id.uuidString
                        ), let apiKey = String(data: data, encoding: .utf8), !apiKey.isEmpty else {
                            throw AccountStoreError.missingCredentials(profile.name)
                        }
                        return (profile.id, .success(try await externalService.fetch(profile: profile, apiKey: apiKey)))
                    } catch {
                        return (profile.id, .failure(error))
                    }
                }
            }

            for await (providerID, result) in group {
                switch result {
                case .success(let usage): providerUsage[providerID] = usage
                case .failure(let error): failures.append(error.localizedDescription)
                }
            }
        }

        persistAccounts()
        isRefreshingUsage = false
        if !isBusy {
            statusText = failures.isEmpty ? "Quota refreshed" : "Quota refreshed with \(failures.count) error(s)"
        }
        let targetCount = accounts.count + quotaProviders.count
        if showErrors, targetCount > 0, failures.count == targetCount, let first = failures.first {
            errorMessage = first
        }
        return failures.isEmpty
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
            _ = await self.refreshUsage(showErrors: false)
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
            _ = await self.refreshUsage(showErrors: false)
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
