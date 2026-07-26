import Foundation

struct ProcessResult {
    let exitCode: Int32
    let output: String
}

enum ProcessRunner {
    static func preparedEnvironment(_ environment: [String: String]) -> [String: String] {
        var prepared = environment
        var paths = (environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin")
            .split(separator: ":")
            .map(String.init)

        for path in ["/opt/homebrew/bin", "/usr/local/bin"] where !paths.contains(path) {
            paths.append(path)
        }

        prepared["PATH"] = paths.joined(separator: ":")
        return prepared
    }

    static func run(
        _ executable: URL,
        arguments: [String],
        environment: [String: String] = ProcessInfo.processInfo.environment,
        timeout: TimeInterval = 30
    ) async throws -> ProcessResult {
        try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            let pipe = Pipe()
            process.executableURL = executable
            process.arguments = arguments
            process.environment = preparedEnvironment(environment)
            process.standardOutput = pipe
            process.standardError = pipe

            let state = LockedContinuation(continuation)
            process.terminationHandler = { process in
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                state.resume(.success(ProcessResult(
                    exitCode: process.terminationStatus,
                    output: String(decoding: data, as: UTF8.self)
                )))
            }

            do {
                try process.run()
            } catch {
                state.resume(.failure(error))
                return
            }

            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                guard process.isRunning else { return }
                process.terminate()
                state.resume(.failure(ProcessRunnerError.timeout(arguments.joined(separator: " "))))
            }
        }
    }

    static func launchDetached(
        _ executable: URL,
        arguments: [String],
        environment: [String: String]
    ) throws -> Process {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = preparedEnvironment(environment)
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        return process
    }
}

private final class LockedContinuation: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<ProcessResult, Error>?

    init(_ continuation: CheckedContinuation<ProcessResult, Error>) {
        self.continuation = continuation
    }

    func resume(_ result: Result<ProcessResult, Error>) {
        lock.lock()
        let current = continuation
        continuation = nil
        lock.unlock()
        current?.resume(with: result)
    }
}

enum ProcessRunnerError: LocalizedError {
    case timeout(String)
    var errorDescription: String? {
        switch self {
        case .timeout(let command): "Timed out: \(command)"
        }
    }
}
