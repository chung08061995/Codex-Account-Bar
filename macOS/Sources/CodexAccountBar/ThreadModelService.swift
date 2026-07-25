import Foundation
import SQLite3

struct ThreadModelService {
    private struct Backup: Codable {
        var activeProviderID: String?
        var activatedAt: Int64?
        var entries: [Entry]
    }

    private struct Entry: Codable {
        let threadID: String
        let providerID: String
        let originalModel: String?
        let routedModel: String
    }

    private let codexHome: URL

    init(codexHome: URL? = nil) {
        self.codexHome = codexHome
            ?? FileManager.default.homeDirectoryForCurrentUser.appending(path: ".codex", directoryHint: .isDirectory)
    }

    func routeExistingThreads(to routedModel: String, providerID: String) throws {
        guard let databaseURL = stateDatabaseURL else { return }
        var backup = loadBackup()
        if backup.activeProviderID != providerID || backup.activatedAt == nil {
            backup.activatedAt = Int64(Date().timeIntervalSince1970)
        }
        backup.activeProviderID = providerID

        try withDatabase(databaseURL) { database in
            let rows = try queryBareOpenAIThreads(database)
            let known = Set(backup.entries.map(\.threadID))
            backup.entries += rows.compactMap { row in
                guard !known.contains(row.id) else { return nil }
                return Entry(
                    threadID: row.id,
                    providerID: providerID,
                    originalModel: row.model,
                    routedModel: routedModel
                )
            }
            // Persist the originals before touching Codex's database so a later restore
            // remains possible even if the process is interrupted during the update.
            try saveBackup(backup)

            try transaction(database) {
                let statement = try prepare(database, "UPDATE threads SET model = ? WHERE id = ?")
                defer { sqlite3_finalize(statement) }
                for row in rows {
                    sqlite3_reset(statement)
                    sqlite3_clear_bindings(statement)
                    sqlite3_bind_text(statement, 1, routedModel, -1, SQLITE_TRANSIENT)
                    sqlite3_bind_text(statement, 2, row.id, -1, SQLITE_TRANSIENT)
                    guard sqlite3_step(statement) == SQLITE_DONE else { throw databaseError(database) }
                }
            }
        }
    }

    func restoreNativeThreads() throws {
        guard let databaseURL = stateDatabaseURL else { return }
        let backup = loadBackup()
        guard let activeProviderID = backup.activeProviderID else { return }

        try withDatabase(databaseURL) { database in
            try transaction(database) {
                let restore = try prepare(
                    database,
                    "UPDATE threads SET model = ? WHERE id = ? AND model = ?"
                )
                defer { sqlite3_finalize(restore) }
                for entry in backup.entries where entry.providerID == activeProviderID {
                    sqlite3_reset(restore)
                    sqlite3_clear_bindings(restore)
                    if let original = entry.originalModel {
                        sqlite3_bind_text(restore, 1, original, -1, SQLITE_TRANSIENT)
                    } else {
                        sqlite3_bind_null(restore, 1)
                    }
                    sqlite3_bind_text(restore, 2, entry.threadID, -1, SQLITE_TRANSIENT)
                    sqlite3_bind_text(restore, 3, entry.routedModel, -1, SQLITE_TRANSIENT)
                    guard sqlite3_step(restore) == SQLITE_DONE else { throw databaseError(database) }
                }

                if let activatedAt = backup.activatedAt {
                    let prefix = activeProviderID + "/"
                    let stripNewThreads = try prepare(
                        database,
                        "UPDATE threads SET model = substr(model, ?) WHERE model_provider = 'openai' AND model LIKE ? AND created_at >= ?"
                    )
                    defer { sqlite3_finalize(stripNewThreads) }
                    sqlite3_bind_int(stripNewThreads, 1, Int32(prefix.count + 1))
                    sqlite3_bind_text(stripNewThreads, 2, prefix + "%", -1, SQLITE_TRANSIENT)
                    sqlite3_bind_int64(stripNewThreads, 3, activatedAt)
                    guard sqlite3_step(stripNewThreads) == SQLITE_DONE else { throw databaseError(database) }
                }
            }
        }

        try saveBackup(Backup(
            activeProviderID: nil,
            activatedAt: nil,
            entries: backup.entries.filter { $0.providerID != activeProviderID }
        ))
    }

    private var stateDatabaseURL: URL? {
        let files = (try? FileManager.default.contentsOfDirectory(
            at: codexHome,
            includingPropertiesForKeys: nil
        )) ?? []
        return files
            .filter { $0.lastPathComponent.range(of: #"^state_[0-9]+\.sqlite$"#, options: .regularExpression) != nil }
            .max { version($0) < version($1) }
    }

    private var backupURL: URL {
        codexHome.appending(path: "accountbar-thread-model-backup.json")
    }

    private func version(_ url: URL) -> Int {
        Int(url.deletingPathExtension().lastPathComponent.split(separator: "_").last ?? "0") ?? 0
    }

    private func loadBackup() -> Backup {
        guard let data = try? Data(contentsOf: backupURL),
              let backup = try? JSONDecoder().decode(Backup.self, from: data)
        else { return Backup(activeProviderID: nil, activatedAt: nil, entries: []) }
        return backup
    }

    private func saveBackup(_ backup: Backup) throws {
        try FileManager.default.createDirectory(at: codexHome, withIntermediateDirectories: true)
        try JSONEncoder().encode(backup).write(to: backupURL, options: .atomic)
    }

    private func withDatabase(_ url: URL, operation: (OpaquePointer) throws -> Void) throws {
        var database: OpaquePointer?
        guard sqlite3_open_v2(url.path, &database, SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK,
              let database else { throw ThreadModelError.openDatabase(url.path) }
        defer { sqlite3_close(database) }
        sqlite3_busy_timeout(database, 5_000)
        try operation(database)
    }

    private func queryBareOpenAIThreads(_ database: OpaquePointer) throws -> [(id: String, model: String?)] {
        let statement = try prepare(
            database,
            "SELECT id, model FROM threads WHERE model_provider = 'openai' AND (model IS NULL OR instr(model, '/') = 0)"
        )
        defer { sqlite3_finalize(statement) }
        var rows: [(String, String?)] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            guard let idValue = sqlite3_column_text(statement, 0) else { continue }
            let id = String(cString: idValue)
            let model = sqlite3_column_text(statement, 1).map { String(cString: $0) }
            rows.append((id, model))
        }
        return rows
    }

    private func transaction(_ database: OpaquePointer, operation: () throws -> Void) throws {
        guard sqlite3_exec(database, "BEGIN IMMEDIATE", nil, nil, nil) == SQLITE_OK else {
            throw databaseError(database)
        }
        do {
            try operation()
            guard sqlite3_exec(database, "COMMIT", nil, nil, nil) == SQLITE_OK else {
                throw databaseError(database)
            }
        } catch {
            sqlite3_exec(database, "ROLLBACK", nil, nil, nil)
            throw error
        }
    }

    private func prepare(_ database: OpaquePointer, _ sql: String) throws -> OpaquePointer {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else { throw databaseError(database) }
        return statement
    }

    private func databaseError(_ database: OpaquePointer) -> ThreadModelError {
        ThreadModelError.database(String(cString: sqlite3_errmsg(database)))
    }
}

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

enum ThreadModelError: LocalizedError {
    case openDatabase(String)
    case database(String)

    var errorDescription: String? {
        switch self {
        case .openDatabase(let path): "Could not open Codex task database at \(path)."
        case .database(let message): "Could not update Codex task models: \(message)"
        }
    }
}
