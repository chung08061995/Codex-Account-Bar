import AppKit
import Foundation

@MainActor
final class CodexService {
    private let fileManager = FileManager.default
    private var loginProcess: Process?
    private var loginWasCancelled = false

    var codexHome: URL {
        if let custom = ProcessInfo.processInfo.environment["CODEX_HOME"], !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        return fileManager.homeDirectoryForCurrentUser.appending(path: ".codex", directoryHint: .isDirectory)
    }

    var authURL: URL { codexHome.appending(path: "auth.json") }

    func activateAccount(authData: Data) throws {
        _ = try JSONSerialization.jsonObject(with: authData)
        try fileManager.createDirectory(at: codexHome, withIntermediateDirectories: true)
        let temporary = codexHome.appending(path: "auth.json.cab.tmp")
        try authData.write(to: temporary, options: [.atomic])
        if fileManager.fileExists(atPath: authURL.path) {
            _ = try fileManager.replaceItemAt(authURL, withItemAt: temporary)
        } else {
            try fileManager.moveItem(at: temporary, to: authURL)
        }
    }

    func activeAccountID(accounts: [SavedAccount]) -> String? {
        guard let active = try? Data(contentsOf: authURL) else { return nil }
        for account in accounts {
            guard let saved = try? KeychainStore.read(
                service: KeychainStore.accountService,
                account: account.id
            ) else { continue }
            if canonicalJSON(saved) == canonicalJSON(active) { return account.id }
        }
        return nil
    }

    func loginIsolated() async throws -> (account: SavedAccount, authData: Data) {
        let executable = try findCodexCLI()
        let temporaryHome = fileManager.temporaryDirectory
            .appending(path: "CodexAccountBar-Login-\(UUID().uuidString)", directoryHint: .isDirectory)
        try fileManager.createDirectory(at: temporaryHome, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: temporaryHome) }

        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = temporaryHome.path
        let result = try await runLoginProcess(
            executable,
            arguments: ["login", "-c", "cli_auth_credentials_store=\"file\""],
            environment: environment
        )
        try Task.checkCancellation()
        let authURL = temporaryHome.appending(path: "auth.json")
        guard result.exitCode == 0, fileManager.fileExists(atPath: authURL.path) else {
            throw CodexServiceError.loginFailed(result.output)
        }
        let authData = try Data(contentsOf: authURL)
        return (try accountMetadata(from: authData), authData)
    }

    func cancelLogin() {
        loginWasCancelled = true
        if let loginProcess, loginProcess.isRunning {
            loginProcess.terminate()
        }
    }

    func restartCodex() async throws {
        let running = NSRunningApplication.runningApplications(
            withBundleIdentifier: "com.openai.codex"
        )
        let applicationURL = running.compactMap(\.bundleURL).first
            ?? NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.openai.codex")
            ?? installedCodexURL()
        guard let applicationURL else { throw CodexServiceError.appMissing }
        for app in running {
            app.terminate()
        }
        if !running.isEmpty {
            try await Task.sleep(for: .milliseconds(900))
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        _ = try await NSWorkspace.shared.openApplication(
            at: applicationURL.resolvingSymlinksInPath(),
            configuration: configuration
        )
    }

    private func installedCodexURL() -> URL? {
        ["/Applications/ChatGPT.app", "/Applications/Codex.app"]
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            .first { fileManager.fileExists(atPath: $0.path) }
    }

    private func canonicalJSON(_ data: Data?) -> Data? {
        guard let data,
              let object = try? JSONSerialization.jsonObject(with: data),
              let canonical = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        else { return nil }
        return canonical
    }

    private func findCodexCLI() throws -> URL {
        let candidates = [
            "/Applications/ChatGPT.app/Contents/Resources/codex",
            "/Applications/Codex.app/Contents/Resources/codex",
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex"
        ]
        guard let path = candidates.first(where: { fileManager.isExecutableFile(atPath: $0) }) else {
            throw CodexServiceError.cliMissing
        }
        return URL(fileURLWithPath: path)
    }

    private func runLoginProcess(
        _ executable: URL,
        arguments: [String],
        environment: [String: String]
    ) async throws -> ProcessResult {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment
        process.standardOutput = pipe
        process.standardError = pipe
        loginProcess = process
        loginWasCancelled = false

        let timeoutTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(600))
            guard let self, process.isRunning else { return }
            self.loginProcess?.terminate()
        }
        defer {
            timeoutTask.cancel()
            loginProcess = nil
        }

        let result = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                process.terminationHandler = { process in
                    let data = pipe.fileHandleForReading.readDataToEndOfFile()
                    continuation.resume(returning: ProcessResult(
                        exitCode: process.terminationStatus,
                        output: String(decoding: data, as: UTF8.self)
                    ))
                }
                do {
                    try process.run()
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.cancelLogin() }
        }

        if loginWasCancelled || Task.isCancelled {
            throw CancellationError()
        }
        return result
    }

    private func accountMetadata(from authData: Data) throws -> SavedAccount {
        guard let root = try JSONSerialization.jsonObject(with: authData) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any],
              let accountID = tokens["account_id"] as? String,
              !accountID.isEmpty
        else {
            throw CodexServiceError.invalidAuth
        }

        var email = "ChatGPT account"
        var plan = "chatgpt"
        if let idToken = tokens["id_token"] as? String,
           let claims = decodeJWTPayload(idToken) {
            email = claims["email"] as? String ?? email
            if let auth = claims["https://api.openai.com/auth"] as? [String: Any] {
                plan = auth["chatgpt_plan_type"] as? String ?? plan
            }
        }
        return SavedAccount(
            id: accountID,
            email: email,
            plan: plan,
            label: "",
            usage: nil,
            lastWarmupAt: nil
        )
    }

    private func decodeJWTPayload(_ token: String) -> [String: Any]? {
        let parts = token.split(separator: ".")
        guard parts.count > 1 else { return nil }
        var encoded = String(parts[1])
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        encoded += String(repeating: "=", count: (4 - encoded.count % 4) % 4)
        guard let data = Data(base64Encoded: encoded),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return object
    }
}

enum CodexServiceError: LocalizedError {
    case cliMissing
    case appMissing
    case loginFailed(String)
    case invalidAuth

    var errorDescription: String? {
        switch self {
        case .cliMissing:
            "Codex CLI was not found."
        case .appMissing:
            "Codex app was not found in /Applications."
        case .loginFailed(let output):
            output.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? "Codex sign-in was cancelled or failed."
                : output.trimmingCharacters(in: .whitespacesAndNewlines)
        case .invalidAuth:
            "The Codex account ID is missing from this login."
        }
    }
}
