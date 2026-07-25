import Foundation

@MainActor
final class ProviderService {
    private var proxyProcess: Process?

    func activate(
        profile: ProviderProfile,
        allProfiles: [ProviderProfile],
        status: @escaping @MainActor (String) -> Void
    ) async throws {
        let ocx = try findOCX()
        let environment = try providerEnvironment(allProfiles)
        let keyReference = profile.requiresAPIKey ? "${\(environmentName(profile.id))}" : nil

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
        _ = try? await ProcessRunner.run(
            ocx,
            arguments: ["stop"],
            environment: environment,
            timeout: 20
        )
        proxyProcess?.terminate()
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
    }

    func activateNativeOpenAI(status: @escaping @MainActor (String) -> Void) async {
        guard let ocx = try? findOCX() else { return }
        status("Restoring native Codex…")
        _ = try? await ProcessRunner.run(
            ocx,
            arguments: ["stop"],
            timeout: 25
        )
        proxyProcess?.terminate()
        proxyProcess = nil
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
        }
    }
}
