import Foundation

@MainActor
final class ProviderService {
    private var proxyProcess: Process?
    private let nativeModelKey = "nativeCodexModel.v1"

    func activate(
        profile: ProviderProfile,
        allProfiles: [ProviderProfile],
        status: @escaping @MainActor (String) -> Void
    ) async throws {
        let ocx = try findOCX()
        let environment = try providerEnvironment(allProfiles)
        let keyReference = profile.requiresAPIKey ? "${\(environmentName(profile.id))}" : nil

        status("Stopping previous opencodex…")
        _ = try? await ProcessRunner.run(
            ocx,
            arguments: ["stop"],
            environment: environment,
            timeout: 20
        )
        proxyProcess?.terminate()
        proxyProcess = nil

        status("Configuring \(profile.name)…")
        var arguments = ["provider", "add", profile.providerID]
        if !profile.registryProvider {
            arguments += ["--adapter", profile.adapter, "--base-url", profile.baseURL]
        }
        if let keyReference { arguments += ["--api-key", keyReference] }
        if !profile.defaultModel.isEmpty {
            arguments += ["--default-model", profile.defaultModel]
        }
        arguments += ["--set-default", "--force", "--json"]

        let add = try await ProcessRunner.run(
            ocx,
            arguments: arguments,
            environment: environment,
            timeout: 45
        )
        guard add.exitCode == 0 else { throw ProviderError.command(add.output) }
        if profile.providerID == "claudible" {
            try configureClaudibleHeaders()
            try clearDirectCodexProvider(profile.providerID)
        }

        if profile.requiresOAuth {
            status("Importing \(profile.name) login…")
            let login = try await ProcessRunner.run(
                ocx,
                arguments: ["login", profile.providerID],
                environment: environment,
                timeout: 180
            )
            guard login.exitCode == 0 else { throw ProviderError.command(login.output) }
        }

        status("Restarting opencodex…")
        proxyProcess = try ProcessRunner.launchDetached(
            ocx,
            arguments: ["start"],
            environment: environment
        )

        var healthy = false
        for _ in 0..<20 {
            try await Task.sleep(for: .milliseconds(350))
            if let result = try? await ProcessRunner.run(
                ocx,
                arguments: ["health", "--json"],
                environment: environment,
                timeout: 5
            ), result.exitCode == 0 {
                healthy = true
                break
            }
        }
        guard healthy else { throw ProviderError.proxyDidNotStart }

        status("Syncing models to Codex…")
        let sync = try await ProcessRunner.run(
            ocx,
            arguments: ["sync"],
            environment: environment,
            timeout: 60
        )
        guard sync.exitCode == 0 else { throw ProviderError.command(sync.output) }

        if !profile.defaultModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            status("Selecting \(profile.defaultModel)…")
            try selectCodexModel(profile.codexModelSlug)
        }
    }

    func activateNativeOpenAI(status: @escaping @MainActor (String) -> Void) async {
        guard let ocx = try? findOCX() else { return }
        status("Restoring native Codex…")
        _ = try? await ProcessRunner.run(
            ocx,
            arguments: ["restore"],
            timeout: 25
        )
        proxyProcess?.terminate()
        proxyProcess = nil
        if let nativeModel = UserDefaults.standard.string(forKey: nativeModelKey) {
            try? CodexConfigEditor.setNativeModel(nativeModel, at: codexConfigURL)
        }
    }

    private func configureClaudibleHeaders() throws {
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appending(path: ".opencodex/config.json")
        let data = try Data(contentsOf: url)
        guard var root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              var providers = root["providers"] as? [String: Any],
              var claudible = providers["claudible"] as? [String: Any]
        else { throw ProviderError.invalidConfig }
        claudible["headers"] = [
            "User-Agent": "codex_cli_rs/0.146.0",
            "originator": "codex_cli_rs"
        ]
        claudible["models"] = [
            "gpt-5.6-sol",
            "gpt-5.6-terra",
            "gpt-5.6-luna",
            "gpt-5.5",
            "gpt-5.4",
            "gpt-5.4-mini"
        ]
        providers["claudible"] = claudible
        root["providers"] = providers
        let updated = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
        try updated.write(to: url, options: .atomic)
    }

    private func clearDirectCodexProvider(_ providerID: String) throws {
        let content = (try? String(contentsOf: codexConfigURL, encoding: .utf8)) ?? ""
        let updated = CodexConfigEditor.removingDirectProvider(providerID, in: content)
        guard updated != content else { return }
        try updated.write(to: codexConfigURL, atomically: true, encoding: .utf8)
    }

    private func selectCodexModel(_ model: String) throws {
        let content = (try? String(contentsOf: codexConfigURL, encoding: .utf8)) ?? ""
        if let current = CodexConfigEditor.currentModel(in: content), !current.contains("/") {
            UserDefaults.standard.set(current, forKey: nativeModelKey)
        }
        try CodexConfigEditor.setModel(model, at: codexConfigURL)
    }

    private var codexConfigURL: URL {
        let home = ProcessInfo.processInfo.environment["CODEX_HOME"]
            .flatMap { $0.isEmpty ? nil : $0 }
            .map(URL.init(fileURLWithPath:))
            ?? FileManager.default.homeDirectoryForCurrentUser.appending(path: ".codex")
        return home.appending(path: "config.toml")
    }

    private func providerEnvironment(_ profiles: [ProviderProfile]) throws -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for profile in profiles where profile.requiresAPIKey {
            guard let data = try KeychainStore.read(
                service: KeychainStore.providerService,
                account: profile.id.uuidString
            ), let value = String(data: data, encoding: .utf8), !value.isEmpty else {
                throw ProviderError.missingAPIKey(profile.name)
            }
            environment[environmentName(profile.id)] = value
        }
        return environment
    }

    private func environmentName(_ id: UUID) -> String {
        "CAB_PROVIDER_\(id.uuidString.replacingOccurrences(of: "-", with: "_"))_API_KEY"
    }

    private func findOCX() throws -> URL {
        let candidates = [
            "/opt/homebrew/bin/ocx",
            "/usr/local/bin/ocx",
            "\(FileManager.default.homeDirectoryForCurrentUser.path)/.npm-global/bin/ocx"
        ]
        if let match = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) {
            return URL(fileURLWithPath: match)
        }
        throw ProviderError.ocxMissing
    }
}

enum ProviderError: LocalizedError {
    case ocxMissing
    case missingAPIKey(String)
    case command(String)
    case proxyDidNotStart
    case invalidConfig

    var errorDescription: String? {
        switch self {
        case .ocxMissing:
            "`ocx` was not found. Install opencodex first."
        case .missingAPIKey(let name):
            "No API key is saved for \(name)."
        case .command(let output):
            output.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? "opencodex command failed."
                : output.trimmingCharacters(in: .whitespacesAndNewlines)
        case .proxyDidNotStart:
            "opencodex did not become healthy."
        case .invalidConfig:
            "The opencodex provider configuration is invalid."
        }
    }
}
