import XCTest
@testable import CodexAccountBar

@MainActor
final class ApplicationRestartCoordinatorTests: XCTestCase {
    func testRestartWaitsForTerminationBeforeOpening() async throws {
        var terminationChecks = 0
        var openedWhileRunning = false
        let target = RestartTarget(
            terminate: {},
            forceTerminate: {},
            isTerminated: {
                terminationChecks += 1
                return terminationChecks >= 4
            }
        )
        let coordinator = ApplicationRestartCoordinator(sleep: { _ in })

        try await coordinator.restart(targets: [target]) {
            openedWhileRunning = !target.isTerminated()
        }

        XCTAssertFalse(openedWhileRunning)
        XCTAssertGreaterThanOrEqual(terminationChecks, 4)
    }

    func testRestartForceTerminatesAfterGracefulTimeout() async throws {
        var terminated = false
        var forceTerminationCount = 0
        var didOpen = false
        let target = RestartTarget(
            terminate: {},
            forceTerminate: {
                forceTerminationCount += 1
                terminated = true
            },
            isTerminated: { terminated }
        )
        let coordinator = ApplicationRestartCoordinator(
            pollInterval: .zero,
            maximumGracefulPolls: 2,
            maximumForcedPolls: 2,
            sleep: { _ in }
        )

        try await coordinator.restart(targets: [target]) {
            didOpen = true
        }

        XCTAssertEqual(forceTerminationCount, 1)
        XCTAssertTrue(didOpen)
    }
}
