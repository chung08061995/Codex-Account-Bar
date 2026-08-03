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
    @Published var autoFailoverEnabled: Bool {
        didSet {
            UserDefaults.standard.set(autoFailoverEnabled, forKey: Self.autoFailoverKey)
        }
    }
    @Published private(set) var isAutoRecovering = false
    @Published var errorMessage: String?

    private let accountMetadataKey = "savedAccountMetadata.v1"
    private let providerMetadataKey = "savedProviderMetadata.v1"
    private let activeProviderKey = "activeProviderID.v1"
    private let codexService = CodexService()
    private let providerService = ProviderService()
    private let usageService = UsageService()
    private let providerUsageService = ProviderUsageService()
    private var addAccountTask: Task<Void, Never>?
    private var activationTask: Task<Void, Never>?
    private var refreshLoopTask: Task<Void, Never>?
    private var failoverMonitorTask: Task<Void, Never>?
    private var lastAutoFailoverAt: Date?

    private let automaticRefreshInterval: UInt64 = 10 * 60 * 1_000_000_000
    private let failoverMonitorInterval: UInt64 = 60 * 1_000_000_000
    private let failoverCooldown: TimeInterval = 10 * 60
    private static let autoFailoverKey = "autoFailoverEnabled.v1"

    var activeQuotaWindow: UsageWindow? {
        if let activeProviderID {
            return providerUsage[activeProviderID]?.primary
        }
        guard let activeAccountID else { return nil }
        return accounts.first(where: { $0.id == activeAccountID })?.usage?.primary
    }

    init() {
        autoFailoverEnabled = UserDefaults.standard.object(forKey: Self.autoFailoverKey) as? Bool ?? false
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
        startFailoverMonitor()
    }

    deinit {
        activationTask?.cancel()
        refreshLoopTask?.cancel()
        failoverMonitorTask?.cancel()
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
        startAccountLogin(expectedAccount: nil)
    }

    func reauthenticateAccount(_ account: SavedAccount) {
        startAccountLogin(expectedAccount: account)
    }

    private func startAccountLogin(expectedAccount: SavedAccount?) {
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
                if let expectedAccount, result.account.id != expectedAccount.id {
                    throw AccountStoreError.unexpectedAccount(expectedAccount.displayName)
                }
                try KeychainStore.write(
                    result.authData,
                    service: KeychainStore.accountService,
                    account: result.account.id
                )
                if let index = accounts.firstIndex(where: { $0.id == result.account.id }) {
                    var updated = result.account
                    updated.label = accounts[index].label
                    updated.lastWarmupAt = accounts[index].lastWarmupAt
                    updated.credentialStatus = .ready
                    accounts[index] = updated
                } else {
                    var added = result.account
                    added.credentialStatus = .ready
                    accounts.append(added)
                }
                persistAccounts()
                _ = await refreshUsage(showErrors: false)
                statusText = expectedAccount == nil
                    ? "Added \(result.account.displayName)"
                    : "Signed in to \(result.account.displayName)"
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

    func cancelCurrentOperation() {
        if isAddingAccount {
            cancelAddAccount()
            return
        }
        guard isBusy else { return }
        statusText = "Cancelling…"
        if isAutoRecovering {
            failoverMonitorTask?.cancel()
            startFailoverMonitor()
        } else {
            activationTask?.cancel()
        }
    }

    private func startFailoverMonitor() {
        failoverMonitorTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: self?.failoverMonitorInterval ?? 0)
                } catch {
                    break
                }
                guard let self else { break }
                await self.monitorActiveQuota()
            }
        }
    }

    func refreshUsage(showErrors: Bool = true) async -> Bool {
        guard !isRefreshingUsage else { return false }
        isRefreshingUsage = true
        if !isBusy { statusText = "Refreshing quota…" }
        var failures: [String] = []
        let service = usageService
        let externalService = providerUsageService
        let activeAuth = codexService.readActiveAuthData()

        // Security.framework can serialize or prompt generic-password reads. A burst
        // of simultaneous SecItemCopyMatching calls can therefore block every quota
        // worker. Snapshot credentials one at a time, then parallelize only HTTP.
        var accountRefreshInputs: [(id: String, auth: Data)] = []
        var keychainTimedOut = false
        for account in accounts {
            do {
                guard let savedAuth = try await KeychainStore.read(
                    service: KeychainStore.accountService,
                    account: account.id
                ) else {
                    throw AccountStoreError.missingCredentials(account.displayName)
                }
                accountRefreshInputs.append((
                    account.id,
                    AccountCredentialResolver.resolve(
                        accountID: account.id,
                        savedAuth: savedAuth,
                        activeAuth: activeAuth
                    )
                ))
            } catch {
                failures.append(error.localizedDescription)
                if error is KeychainReadError {
                    keychainTimedOut = true
                    break
                }
            }
        }

        await withTaskGroup(of: (String, Data, Result<AccountUsageRefresh, Error>).self) { group in
            for input in accountRefreshInputs {
                group.addTask {
                    do {
                        let refreshed = try await service.fetchRefreshingCredential(authData: input.auth)
                        return (input.id, input.auth, .success(refreshed))
                    } catch {
                        return (input.id, input.auth, .failure(error))
                    }
                }
            }

            for await (accountID, sourceAuth, result) in group {
                guard let index = accounts.firstIndex(where: { $0.id == accountID }) else { continue }
                switch result {
                case .success(let refreshed):
                    do {
                        try commitRefreshedCredential(
                            accountID: accountID,
                            sourceAuth: sourceAuth,
                            refreshedAuth: refreshed.authData
                        )
                        accounts[index].usage = refreshed.usage
                        accounts[index].credentialStatus = .ready
                    } catch {
                        failures.append(error.localizedDescription)
                    }
                case .failure(let error):
                    if AccountAuthenticationFailure.isCredentialFailure(error) {
                        // Never auto-switch using a stale cached 100% quota whose login
                        // can no longer be refreshed.
                        accounts[index].usage = nil
                        accounts[index].credentialStatus = .signInRequired
                    }
                    failures.append(error.localizedDescription)
                }
            }
        }

        let quotaProviders = providers.filter { $0.providerID == "claudible" && $0.requiresAPIKey }
        var providerRefreshInputs: [(profile: ProviderProfile, apiKey: String)] = []
        for profile in quotaProviders where !keychainTimedOut {
            do {
                guard let data = try await KeychainStore.read(
                    service: KeychainStore.providerService,
                    account: profile.id.uuidString
                ), let apiKey = String(data: data, encoding: .utf8), !apiKey.isEmpty else {
                    throw AccountStoreError.missingCredentials(profile.name)
                }
                providerRefreshInputs.append((profile, apiKey))
            } catch {
                failures.append(error.localizedDescription)
            }
        }
        await withTaskGroup(of: (UUID, Result<UsageSnapshot, Error>).self) { group in
            for input in providerRefreshInputs {
                group.addTask {
                    do {
                        return (
                            input.profile.id,
                            .success(try await externalService.fetch(
                                profile: input.profile,
                                apiKey: input.apiKey
                            ))
                        )
                    } catch {
                        return (input.profile.id, .failure(error))
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
            let resetApplied = accounts.contains { $0.usage?.automaticResetApplied == true }
            if resetApplied {
                statusText = "Quota exhausted; available reset applied"
            } else {
                statusText = failures.isEmpty ? "Quota refreshed" : "Quota refreshed with \(failures.count) error(s)"
            }
        }
        let targetCount = accounts.count + quotaProviders.count
        if showErrors, targetCount > 0, failures.count == targetCount, let first = failures.first {
            errorMessage = first
        }
        return failures.isEmpty
    }

    func activateAccount(_ account: SavedAccount) {
        perform {
            do {
                try self.persistActiveCredentialToKeychain()
                self.statusText = "Checking \(account.displayName)…"
                guard let savedAuth = try await KeychainStore.read(
                    service: KeychainStore.accountService,
                    account: account.id
                ) else {
                    throw AccountStoreError.missingCredentials(account.displayName)
                }
                let refreshed = try await self.usageService.fetchRefreshingCredential(authData: savedAuth)
                if refreshed.authData != savedAuth {
                    try KeychainStore.write(
                        refreshed.authData,
                        service: KeychainStore.accountService,
                        account: account.id
                    )
                }
                if let index = self.accounts.firstIndex(where: { $0.id == account.id }) {
                    self.accounts[index].usage = refreshed.usage
                    self.accounts[index].credentialStatus = .ready
                    self.persistAccounts()
                }
                self.statusText = "Switching to \(account.displayName)…"
                await self.providerService.activateNativeOpenAI { self.statusText = $0 }
                try self.codexService.activateAccount(authData: refreshed.authData)
                self.activeProviderID = nil
                UserDefaults.standard.removeObject(forKey: self.activeProviderKey)
                self.activeAccountID = account.id
                self.statusText = "Restarting Codex…"
                try await self.codexService.restartCodex()
                _ = await self.refreshUsage(showErrors: false)
                self.statusText = "Using \(account.displayName)"
            } catch {
                self.markCredentialFailure(accountID: account.id, error: error)
                throw error
            }
        }
    }

    func activateProvider(_ profile: ProviderProfile) {
        perform {
            try self.persistActiveCredentialToKeychain()
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

    private func monitorActiveQuota() async {
        guard autoFailoverEnabled,
              !isBusy,
              !isRefreshingUsage,
              !isAutoRecovering,
              activeProviderID == nil,
              let activeAccountID,
              let index = accounts.firstIndex(where: { $0.id == activeAccountID })
        else { return }

        do {
            guard let savedAuth = try await KeychainStore.read(
                service: KeychainStore.accountService,
                account: activeAccountID
            ) else {
                throw AccountStoreError.missingCredentials(accounts[index].displayName)
            }
            let auth = AccountCredentialResolver.resolve(
                accountID: activeAccountID,
                savedAuth: savedAuth,
                activeAuth: codexService.readActiveAuthData()
            )
            let refreshed = try await usageService.fetchRefreshingCredential(authData: auth)
            try commitRefreshedCredential(
                accountID: activeAccountID,
                sourceAuth: auth,
                refreshedAuth: refreshed.authData
            )
            accounts[index].usage = refreshed.usage
            accounts[index].credentialStatus = .ready
            persistAccounts()
            guard QuotaFailoverPolicy.isExhausted(refreshed.usage) else { return }
            await attemptAutoFailover(exhaustedAccountID: activeAccountID)
        } catch {
            markCredentialFailure(accountID: activeAccountID, error: error)
            statusText = "Auto-switch check failed; retrying"
        }
    }

    private func attemptAutoFailover(exhaustedAccountID: String) async {
        guard autoFailoverEnabled, !isAutoRecovering else { return }
        if let lastAutoFailoverAt,
           Date().timeIntervalSince(lastAutoFailoverAt) < failoverCooldown {
            return
        }

        isAutoRecovering = true
        isBusy = true
        errorMessage = nil
        defer {
            isBusy = false
            isAutoRecovering = false
        }

        var selectedCandidateID: String?
        do {
            try persistActiveCredentialToKeychain()
        } catch {
            statusText = "Automatic account switch failed"
            errorMessage = error.localizedDescription
            return
        }

        statusText = "Quota exhausted; checking other accounts…"
        _ = await refreshUsage(showErrors: false)
        guard !Task.isCancelled else {
            statusText = "Automatic account switch cancelled"
            return
        }
        guard let exhausted = accounts.first(where: { $0.id == exhaustedAccountID }),
              QuotaFailoverPolicy.isExhausted(exhausted.usage)
        else {
            statusText = "Quota restored; staying on current account"
            return
        }
        guard let candidate = QuotaFailoverPolicy.bestCandidate(
            from: accounts,
            excludingAccountID: exhaustedAccountID
        ) else {
            statusText = "Quota exhausted; no other account has usable quota"
            return
        }
        selectedCandidateID = candidate.id

        do {
            guard let savedAuth = try await KeychainStore.read(
                service: KeychainStore.accountService,
                account: candidate.id
            ) else {
                throw AccountStoreError.missingCredentials(candidate.displayName)
            }
            let refreshed = try await usageService.fetchRefreshingCredential(authData: savedAuth)
            try Task.checkCancellation()
            if refreshed.authData != savedAuth {
                try KeychainStore.write(
                    refreshed.authData,
                    service: KeychainStore.accountService,
                    account: candidate.id
                )
            }

            let recovery = CodexTaskRecoveryService(
                codexHome: codexService.codexHome,
                codexExecutable: try codexService.findCodexCLI()
            )
            statusText = "Finding tasks stopped by quota…"
            var taskScanError: String?
            let tasks: [RecoverableCodexTask]
            do {
                tasks = try await recovery.findUsageLimitedTasks()
            } catch {
                tasks = []
                taskScanError = error.localizedDescription
            }
            try Task.checkCancellation()
            let applicationURL = try await codexService.terminateCodex()
            try Task.checkCancellation()
            do {
                await providerService.activateNativeOpenAI { self.statusText = $0 }
                try Task.checkCancellation()
                try codexService.activateAccount(authData: refreshed.authData)
                activeProviderID = nil
                UserDefaults.standard.removeObject(forKey: activeProviderKey)
                activeAccountID = candidate.id
                lastAutoFailoverAt = Date()

                var recoveryResult = CodexTaskRecoveryResult(
                    attempted: 0,
                    started: 0,
                    failures: []
                )
                if !tasks.isEmpty {
                    statusText = "Starting \(tasks.count) stopped task(s) on \(candidate.displayName)…"
                    recoveryResult = await recovery.resume(tasks)
                }
                statusText = "Reopening Codex…"
                try await codexService.launchCodex(at: applicationURL)
                try Task.checkCancellation()
                _ = await refreshUsage(showErrors: false)
                if let taskScanError {
                    statusText = "Switched account; task recovery scan failed"
                    errorMessage = taskScanError
                } else if recoveryResult.failures.isEmpty {
                    statusText = tasks.isEmpty
                        ? "Switched to \(candidate.displayName)"
                        : "Switched account; started \(recoveryResult.started) task(s)"
                } else {
                    statusText = "Switched account; started \(recoveryResult.started)/\(recoveryResult.attempted) task(s)"
                    errorMessage = recoveryResult.failures.joined(separator: "\n")
                }
            } catch {
                try? await codexService.launchCodex(at: applicationURL)
                throw error
            }
        } catch is CancellationError {
            statusText = "Automatic account switch cancelled"
        } catch {
            if let selectedCandidateID {
                markCredentialFailure(accountID: selectedCandidateID, error: error)
            }
            statusText = "Automatic account switch failed"
            errorMessage = error.localizedDescription
        }
    }

    private func perform(_ operation: @escaping @MainActor () async throws -> Void) {
        guard !isBusy else { return }
        isBusy = true
        errorMessage = nil
        activationTask = Task {
            defer {
                isBusy = false
                activationTask = nil
            }
            do {
                try Task.checkCancellation()
                try await operation()
                try Task.checkCancellation()
            } catch is CancellationError {
                statusText = "Operation cancelled"
            } catch {
                errorMessage = error.localizedDescription
                statusText = "Activation failed"
            }
        }
    }

    private func persistActiveCredentialToKeychain() throws {
        guard let activeAuth = codexService.readActiveAuthData(),
              let accountID = AccountCredentialResolver.authAccountID(activeAuth),
              accounts.contains(where: { $0.id == accountID })
        else { return }
        try KeychainStore.write(
            activeAuth,
            service: KeychainStore.accountService,
            account: accountID
        )
    }

    private func commitRefreshedCredential(
        accountID: String,
        sourceAuth: Data,
        refreshedAuth: Data
    ) throws {
        let commit = AccountCredentialResolver.refreshCommit(
            accountID: accountID,
            sourceAuth: sourceAuth,
            refreshedAuth: refreshedAuth,
            latestActiveAuth: codexService.readActiveAuthData()
        )
        // Keep Codex's live auth ahead of Keychain so a crash cannot leave
        // the running app holding a refresh token that Bar already rotated.
        if let activeAuth = commit.activeAuth {
            try codexService.activateAccount(authData: activeAuth)
        }
        try KeychainStore.write(
            commit.savedAuth,
            service: KeychainStore.accountService,
            account: accountID
        )
    }

    private func markCredentialFailure(accountID: String, error: Error) {
        guard AccountAuthenticationFailure.isCredentialFailure(error),
              let index = accounts.firstIndex(where: { $0.id == accountID })
        else { return }
        accounts[index].usage = nil
        accounts[index].credentialStatus = .signInRequired
        persistAccounts()
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
    case unexpectedAccount(String)
    var errorDescription: String? {
        switch self {
        case .missingCredentials(let account): "Credentials are missing for \(account)."
        case .unexpectedAccount(let account): "Sign in to \(account), not another account."
        }
    }
}
