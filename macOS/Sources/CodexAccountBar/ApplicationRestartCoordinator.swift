import Foundation

struct RestartTarget {
    let terminate: () -> Void
    let forceTerminate: () -> Void
    let isTerminated: () -> Bool
}

@MainActor
struct ApplicationRestartCoordinator {
    var pollInterval: Duration = .milliseconds(100)
    var maximumGracefulPolls = 50
    var maximumForcedPolls = 30
    var sleep: (Duration) async throws -> Void = { duration in
        try await Task.sleep(for: duration)
    }

    func restart(
        targets: [RestartTarget],
        open: () async throws -> Void
    ) async throws {
        try await terminate(targets: targets)
        try await open()
    }

    func terminate(targets: [RestartTarget]) async throws {
        targets.forEach { $0.terminate() }
        if !targets.isEmpty,
           try await !waitForTermination(targets, maximumPolls: maximumGracefulPolls) {
            targets.filter { !$0.isTerminated() }.forEach { $0.forceTerminate() }
            guard try await waitForTermination(targets, maximumPolls: maximumForcedPolls) else {
                throw ApplicationRestartError.terminationTimedOut
            }
        }
    }

    private func waitForTermination(
        _ targets: [RestartTarget],
        maximumPolls: Int
    ) async throws -> Bool {
        if targets.allSatisfy({ $0.isTerminated() }) {
            return true
        }
        for _ in 0..<maximumPolls {
            try await sleep(pollInterval)
            if targets.allSatisfy({ $0.isTerminated() }) {
                return true
            }
        }
        return targets.allSatisfy { $0.isTerminated() }
    }
}

enum ApplicationRestartError: LocalizedError {
    case terminationTimedOut

    var errorDescription: String? {
        switch self {
        case .terminationTimedOut:
            "Codex did not finish closing, so it could not be restarted safely."
        }
    }
}

@MainActor
struct ApplicationLaunchCoordinator {
    typealias Completion = @Sendable (Result<Void, Error>) -> Void

    private let scheduleTimeout: (@escaping @MainActor () -> Void) -> Void

    init(
        timeout: TimeInterval = 15,
        scheduleTimeout: ((@escaping @MainActor () -> Void) -> Void)? = nil
    ) {
        self.scheduleTimeout = scheduleTimeout ?? { action in
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(timeout))
                action()
            }
        }
    }

    func launchIfNeeded(
        isRunning: () -> Bool,
        open: (@escaping Completion) -> Void
    ) async throws {
        try Task.checkCancellation()
        guard !isRunning() else { return }

        let state = ApplicationLaunchContinuation()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                state.install(continuation)
                open { state.resume($0) }
                scheduleTimeout {
                    state.resume(.failure(ApplicationLaunchError.timedOut))
                }
            }
        } onCancel: {
            state.resume(.failure(CancellationError()))
        }
    }
}

enum ApplicationLaunchError: Error, Equatable, LocalizedError {
    case timedOut

    var errorDescription: String? {
        "Codex launch timed out. If Codex is already open, the account switch is complete."
    }
}

private final class ApplicationLaunchContinuation: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    private var pendingResult: Result<Void, Error>?
    private var completed = false

    func install(_ continuation: CheckedContinuation<Void, Error>) {
        lock.lock()
        if let pendingResult {
            completed = true
            lock.unlock()
            continuation.resume(with: pendingResult)
        } else {
            self.continuation = continuation
            lock.unlock()
        }
    }

    func resume(_ result: Result<Void, Error>) {
        lock.lock()
        guard !completed else {
            lock.unlock()
            return
        }
        if let continuation {
            self.continuation = nil
            completed = true
            lock.unlock()
            continuation.resume(with: result)
        } else if pendingResult == nil {
            pendingResult = result
            lock.unlock()
        } else {
            lock.unlock()
        }
    }
}
