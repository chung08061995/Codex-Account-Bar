import XCTest
@testable import CodexAccountBar

final class ModelsTests: XCTestCase {
    func testLegacyAccountMetadataDecodes() throws {
        let data = Data(#"[{"id":"abc","email":"user@example.com","plan":"plus","label":"","usage":{"primary":{"usedPercent":42}}}]"#.utf8)
        let accounts = try JSONDecoder().decode([SavedAccount].self, from: data)
        XCTAssertEqual(accounts.first?.displayName, "user@example.com")
        XCTAssertEqual(accounts.first?.usage?.primary?.usedPercent, 42)
    }

    func testClaudiblePresetUsesDirectResponsesEndpoint() {
        let profile = ProviderProfile.preset(.claudible)
        XCTAssertEqual(profile.adapter, "openai-responses")
        XCTAssertEqual(profile.baseURL, "https://claude.claudible.io/v1")
        XCTAssertEqual(profile.defaultModel, "gpt-5.6-sol")
        XCTAssertEqual(profile.codexModelSlug, "claudible/gpt-5.6-sol")
    }

    func testCodexConfigEditorReplacesOnlyRootModel() {
        let original = """
        model_reasoning_effort = "medium"
        model = "gpt-5.6-sol"

        [profiles.work]
        model = "gpt-5.5"
        """
        let updated = CodexConfigEditor.settingModel("claudible/claude-sonnet-5", in: original)
        XCTAssertEqual(CodexConfigEditor.currentModel(in: updated), "claudible/claude-sonnet-5")
        XCTAssertTrue(updated.contains("[profiles.work]\nmodel = \"gpt-5.5\""))
    }

    func testCodexConfigEditorInsertsMissingRootModel() {
        let updated = CodexConfigEditor.settingModel("google/gemini-3-pro", in: "[features]\nweb_search = true")
        XCTAssertTrue(updated.hasPrefix("model = \"google/gemini-3-pro\"\n"))
    }

    func testCodexConfigEditorWritesDirectProviderWithoutChangingProfiles() {
        let original = "model = \"gpt-5.6-sol\"\n\n[profiles.work]\nmodel = \"gpt-5.5\""
        let updated = CodexConfigEditor.settingDirectProvider(
            id: "claudible",
            name: "Claudible",
            baseURL: "https://claude.claudible.io/v1",
            bearerToken: "test-key",
            model: "gpt-5.6-sol",
            in: original
        )
        XCTAssertTrue(updated.contains("model_provider = \"claudible\""))
        XCTAssertTrue(updated.contains("[model_providers.claudible]"))
        XCTAssertTrue(updated.contains("experimental_bearer_token = \"test-key\""))
        XCTAssertTrue(updated.contains("[profiles.work]\nmodel = \"gpt-5.5\""))
        let native = CodexConfigEditor.settingNativeModel("gpt-5.6-sol", in: updated)
        XCTAssertFalse(native.contains("model_provider = \"claudible\""))
    }

    func testUsageWindowCalculatesRemainingQuota() {
        let window = UsageWindow(usedPercent: 73, resetAt: nil, windowSeconds: 604_800)
        XCTAssertEqual(window.remainingPercent, 27)
        XCTAssertEqual(window.title, "Weekly")
    }

    func testUsageWindowClampsRemainingQuota() {
        XCTAssertEqual(UsageWindow(usedPercent: 140).remainingPercent, 0)
        XCTAssertEqual(UsageWindow(usedPercent: -5).remainingPercent, 100)
    }
}
