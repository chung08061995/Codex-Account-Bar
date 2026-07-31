import Foundation

struct RecoverableCodexTask: Sendable, Hashable {
    let id: String
    let title: String
    let workingDirectory: String
}

struct CodexTaskRecoveryResult: Sendable {
    let attempted: Int
    let started: Int
    let failures: [String]
}

struct CodexTaskRecoveryService: Sendable {
    private let codexHome: URL
    private let codexExecutable: URL

    init(codexHome: URL, codexExecutable: URL) {
        self.codexHome = codexHome
        self.codexExecutable = codexExecutable
    }

    func findUsageLimitedTasks(
        since: Date = Date().addingTimeInterval(-30 * 60),
        maximumCount: Int = 5
    ) async throws -> [RecoverableCodexTask] {
        let codexHome = codexHome
        let executable = codexExecutable
        return try await Task.detached {
            try AppServerProbe(
                codexHome: codexHome,
                executable: executable
            ).findUsageLimitedTasks(since: since, maximumCount: maximumCount)
        }.value
    }

    func resume(_ tasks: [RecoverableCodexTask]) async -> CodexTaskRecoveryResult {
        guard !tasks.isEmpty else {
            return CodexTaskRecoveryResult(attempted: 0, started: 0, failures: [])
        }
        var started = 0
        var failures: [String] = []
        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = codexHome.path
        let prompt = "The previous account ran out of Codex quota. Continue this task from where it stopped. Preserve existing work, finish the original objective, and verify the result before stopping."

        for task in tasks {
            do {
                let directory = URL(fileURLWithPath: task.workingDirectory, isDirectory: true)
                _ = try ProcessRunner.launchDetached(
                    codexExecutable,
                    arguments: [
                        "exec", "resume", "--skip-git-repo-check",
                        task.id, prompt
                    ],
                    environment: environment,
                    currentDirectoryURL: directory
                )
                started += 1
            } catch {
                failures.append("\(task.title): \(error.localizedDescription)")
            }
        }
        return CodexTaskRecoveryResult(
            attempted: tasks.count,
            started: started,
            failures: failures
        )
    }
}

private final class AppServerProbe {
    private let codexHome: URL
    private let executable: URL
    private var nextRequestID = 1

    init(codexHome: URL, executable: URL) {
        self.codexHome = codexHome
        self.executable = executable
    }

    func findUsageLimitedTasks(
        since: Date,
        maximumCount: Int
    ) throws -> [RecoverableCodexTask] {
        let process = Process()
        let input = Pipe()
        let output = Pipe()
        var environment = ProcessInfo.processInfo.environment
        environment["CODEX_HOME"] = codexHome.path
        process.executableURL = executable
        process.arguments = ["app-server"]
        process.environment = ProcessRunner.preparedEnvironment(environment)
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let deadline = ProcessDeadline(process: process)
        deadline.schedule(after: 30)
        defer {
            deadline.cancel()
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
        }

        let reader = JSONLineReader(handle: output.fileHandleForReading)
        _ = try request(
            method: "initialize",
            params: [
                "clientInfo": [
                    "name": "codex_account_bar",
                    "title": "Codex Account Bar",
                    "version": "2.2.0"
                ],
                "capabilities": ["experimentalApi": true]
            ],
            input: input.fileHandleForWriting,
            reader: reader
        )
        try send(
            ["method": "initialized", "params": [:]],
            to: input.fileHandleForWriting
        )
        let list = try request(
            method: "thread/list",
            params: [
                "limit": 30,
                "sortKey": "updated_at",
                "sortDirection": "desc",
                "sourceKinds": ["vscode", "cli", "appServer", "exec"]
            ],
            input: input.fileHandleForWriting,
            reader: reader
        )
        let cutoff = Int(since.timeIntervalSince1970)
        let rows = (list["data"] as? [[String: Any]] ?? []).filter { row in
            let updatedAt = (row["updatedAt"] as? NSNumber)?.intValue ?? 0
            let isRoot = row["parentThreadId"] == nil || row["parentThreadId"] is NSNull
            return updatedAt >= cutoff && isRoot
        }

        var tasks: [RecoverableCodexTask] = []
        for row in rows where tasks.count < maximumCount {
            guard let threadID = row["id"] as? String else { continue }
            let turnsPage = try request(
                method: "thread/turns/list",
                params: [
                    "threadId": threadID,
                    "limit": 1,
                    "sortDirection": "desc",
                    "itemsView": "summary"
                ],
                input: input.fileHandleForWriting,
                reader: reader
            )
            guard let turns = turnsPage["data"] as? [[String: Any]],
                  let lastTurn = turns.last,
                  lastTurn["status"] as? String == "failed",
                  let error = lastTurn["error"] as? [String: Any],
                  error["codexErrorInfo"] as? String == "usageLimitExceeded"
            else { continue }

            let name = (row["name"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
            let preview = (row["preview"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
            let title = [name, preview].compactMap { $0 }.first(where: { !$0.isEmpty }) ?? threadID
            let workingDirectory = row["cwd"] as? String ?? codexHome.deletingLastPathComponent().path
            tasks.append(RecoverableCodexTask(
                id: threadID,
                title: String(title.prefix(80)),
                workingDirectory: workingDirectory
            ))
        }
        return tasks
    }

    private func request(
        method: String,
        params: [String: Any],
        input: FileHandle,
        reader: JSONLineReader
    ) throws -> [String: Any] {
        let requestID = nextRequestID
        nextRequestID += 1
        try send(
            ["method": method, "id": requestID, "params": params],
            to: input
        )
        while let message = try reader.nextObject() {
            guard (message["id"] as? NSNumber)?.intValue == requestID else { continue }
            if let error = message["error"] as? [String: Any] {
                throw CodexTaskRecoveryError.appServer(
                    error["message"] as? String ?? "Unknown app-server error"
                )
            }
            guard let result = message["result"] as? [String: Any] else {
                throw CodexTaskRecoveryError.invalidResponse(method)
            }
            return result
        }
        throw CodexTaskRecoveryError.appServerStopped
    }

    private func send(_ object: [String: Any], to handle: FileHandle) throws {
        var data = try JSONSerialization.data(withJSONObject: object)
        data.append(0x0A)
        try handle.write(contentsOf: data)
    }
}

private final class ProcessDeadline: @unchecked Sendable {
    private let lock = NSLock()
    private let process: Process
    private var cancelled = false

    init(process: Process) {
        self.process = process
    }

    func schedule(after seconds: TimeInterval) {
        DispatchQueue.global().asyncAfter(deadline: .now() + seconds) { [self] in
            lock.lock()
            let shouldTerminate = !cancelled
            lock.unlock()
            if shouldTerminate, process.isRunning { process.terminate() }
        }
    }

    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
    }
}

private final class JSONLineReader {
    private let handle: FileHandle
    private var buffer = Data()

    init(handle: FileHandle) {
        self.handle = handle
    }

    func nextObject() throws -> [String: Any]? {
        while true {
            if let newline = buffer.firstIndex(of: 0x0A) {
                let line = buffer[..<newline]
                buffer.removeSubrange(...newline)
                guard !line.isEmpty else { continue }
                return try JSONSerialization.jsonObject(with: Data(line)) as? [String: Any]
            }
            let chunk = handle.availableData
            guard !chunk.isEmpty else {
                return nil
            }
            buffer.append(chunk)
        }
    }
}

enum CodexTaskRecoveryError: LocalizedError {
    case appServer(String)
    case appServerStopped
    case invalidResponse(String)

    var errorDescription: String? {
        switch self {
        case .appServer(let message): "Codex task scan failed: \(message)"
        case .appServerStopped: "Codex task scan stopped unexpectedly."
        case .invalidResponse(let method): "Codex returned an invalid response for \(method)."
        }
    }
}
