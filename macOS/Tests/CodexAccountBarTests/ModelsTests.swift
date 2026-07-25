import XCTest
@testable import CodexAccountBar

final class ModelsTests: XCTestCase {
    func testLegacyAccountMetadataDecodes() throws {
        let data = Data(#"[{"id":"abc","email":"user@example.com","plan":"plus","label":"","usage":{"primary":{"usedPercent":42}}}]"#.utf8)
        let accounts = try JSONDecoder().decode([SavedAccount].self, from: data)
        XCTAssertEqual(accounts.first?.displayName, "user@example.com")
        XCTAssertEqual(accounts.first?.usage?.primary?.usedPercent, 42)
    }

    func testClaudiblePresetUsesAnthropicAdapter() {
        let profile = ProviderProfile.preset(.claudible)
        XCTAssertEqual(profile.adapter, "anthropic")
        XCTAssertEqual(profile.defaultModel, "claude-sonnet-5")
    }

    func testUsageWindowCalculatesRemainingQuota() {
        let window = UsageWindow(usedPercent: 73, resetAt: nil, windowSeconds: 604_800)
        XCTAssertEqual(window.remainingPercent, 27)
        XCTAssertEqual(window.title, "Weekly")
    }
}
