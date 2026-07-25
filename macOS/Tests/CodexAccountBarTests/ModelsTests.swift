import XCTest
import SQLite3
@testable import CodexAccountBar

final class ModelsTests: XCTestCase {
    func testLegacyAccountMetadataDecodes() throws {
        let data = Data(#"[{"id":"abc","email":"user@example.com","plan":"plus","label":"","usage":{"primary":{"usedPercent":42}}}]"#.utf8)
        let accounts = try JSONDecoder().decode([SavedAccount].self, from: data)
        XCTAssertEqual(accounts.first?.displayName, "user@example.com")
        XCTAssertEqual(accounts.first?.usage?.primary?.usedPercent, 42)
    }

    func testClaudiblePresetUsesCodexResponsesEndpoint() {
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
        let removed = CodexConfigEditor.removingDirectProvider("claudible", in: updated)
        XCTAssertFalse(removed.contains("[model_providers.claudible]"))
        XCTAssertFalse(removed.contains("test-key"))
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

    func testUsageWindowSupportsProviderTitle() {
        let window = UsageWindow(usedPercent: 25, windowSeconds: 86_400, customTitle: "Daily")
        XCTAssertEqual(window.title, "Daily")
        XCTAssertEqual(window.remainingPercent, 75)
    }

    func testThreadModelsRouteAndRestoreWithoutTouchingExistingRoutedTask() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "AccountBarThreadTest-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let databaseURL = home.appending(path: "state_5.sqlite")
        var database: OpaquePointer?
        XCTAssertEqual(sqlite3_open(databaseURL.path, &database), SQLITE_OK)
        guard let database else { return XCTFail("Could not create test database") }
        defer { sqlite3_close(database) }
        XCTAssertEqual(sqlite3_exec(database, "CREATE TABLE threads (id TEXT PRIMARY KEY, model_provider TEXT NOT NULL, model TEXT, created_at INTEGER NOT NULL)", nil, nil, nil), SQLITE_OK)
        let old = Int64(Date().timeIntervalSince1970) - 60
        XCTAssertEqual(sqlite3_exec(database, "INSERT INTO threads VALUES ('native', 'openai', 'gpt-5.5', \(old))", nil, nil, nil), SQLITE_OK)
        XCTAssertEqual(sqlite3_exec(database, "INSERT INTO threads VALUES ('manual', 'openai', 'claudible/gpt-5.4', \(old))", nil, nil, nil), SQLITE_OK)

        let service = ThreadModelService(codexHome: home)
        try service.routeExistingThreads(to: "claudible/gpt-5.6-sol", providerID: "claudible")
        XCTAssertEqual(try model("native", database: database), "claudible/gpt-5.6-sol")
        XCTAssertEqual(try model("manual", database: database), "claudible/gpt-5.4")

        let now = Int64(Date().timeIntervalSince1970) + 1
        XCTAssertEqual(sqlite3_exec(database, "INSERT INTO threads VALUES ('new', 'openai', 'claudible/gpt-5.6-sol', \(now))", nil, nil, nil), SQLITE_OK)
        try service.restoreNativeThreads()
        XCTAssertEqual(try model("native", database: database), "gpt-5.5")
        XCTAssertEqual(try model("new", database: database), "gpt-5.6-sol")
        XCTAssertEqual(try model("manual", database: database), "claudible/gpt-5.4")
    }

    private func model(_ id: String, database: OpaquePointer) throws -> String? {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, "SELECT model FROM threads WHERE id = ?", -1, &statement, nil) == SQLITE_OK,
              let statement else { throw NSError(domain: "ModelsTests", code: 1) }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_text(statement, 1, id, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self))
        guard sqlite3_step(statement) == SQLITE_ROW else { return nil }
        return sqlite3_column_text(statement, 0).map { String(cString: $0) }
    }
}
