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
        targets.forEach { $0.terminate() }
        if !targets.isEmpty,
           try await !waitForTermination(targets, maximumPolls: maximumGracefulPolls) {
            targets.filter { !$0.isTerminated() }.forEach { $0.forceTerminate() }
            guard try await waitForTermination(targets, maximumPolls: maximumForcedPolls) else {
                throw ApplicationRestartError.terminationTimedOut
            }
        }
        try await open()
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
