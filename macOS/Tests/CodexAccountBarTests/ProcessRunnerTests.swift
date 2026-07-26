import XCTest
@testable import CodexAccountBar

final class ProcessRunnerTests: XCTestCase {
    func testPreparedEnvironmentAddsHomebrewBinaryDirectories() {
        let environment = ProcessRunner.preparedEnvironment([
            "PATH": "/usr/bin:/bin"
        ])

        let paths = environment["PATH"]?.split(separator: ":").map(String.init) ?? []
        XCTAssertTrue(paths.contains("/opt/homebrew/bin"))
        XCTAssertTrue(paths.contains("/usr/local/bin"))
        XCTAssertEqual(paths.filter { $0 == "/usr/bin" }.count, 1)
    }

    func testPreparedEnvironmentPreservesExistingPathEntriesWithoutDuplicates() {
        let environment = ProcessRunner.preparedEnvironment([
            "PATH": "/custom/bin:/opt/homebrew/bin:/usr/bin"
        ])

        let paths = environment["PATH"]?.split(separator: ":").map(String.init) ?? []
        XCTAssertEqual(paths.first, "/custom/bin")
        XCTAssertEqual(paths.filter { $0 == "/opt/homebrew/bin" }.count, 1)
    }
}
