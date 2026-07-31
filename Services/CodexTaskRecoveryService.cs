using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountBar.Services;

public sealed record RecoverableCodexTask(string Id, string Title, string WorkingDirectory);
public sealed record CodexTaskRecoveryResult(int Attempted, int Started, IReadOnlyList<string> Failures);

public sealed class CodexTaskRecoveryService(string codexHome, string codexExecutable)
{
    public async Task<IReadOnlyList<RecoverableCodexTask>> FindUsageLimitedTasksAsync(
        DateTimeOffset? since = null,
        int maximumCount = 5
    )
    {
        await using var server = await AppServerProbe.StartAsync(codexHome, codexExecutable);
        return await server.FindUsageLimitedTasksAsync(
            since ?? DateTimeOffset.UtcNow.AddMinutes(-30),
            maximumCount
        );
    }

    public Task<CodexTaskRecoveryResult> ResumeAsync(
        IReadOnlyList<RecoverableCodexTask> tasks
    )
    {
        var started = 0;
        var failures = new List<string>();
        const string prompt = "The previous account ran out of Codex quota. Continue this task from where it stopped. Preserve existing work, finish the original objective, and verify the result before stopping.";
        foreach (var task in tasks)
        {
            try
            {
                ProcessRunner.LaunchDetached(
                    codexExecutable,
                    ["exec", "resume", "--skip-git-repo-check", task.Id, prompt],
                    env: new Dictionary<string, string?> { ["CODEX_HOME"] = codexHome },
                    workingDirectory: Directory.Exists(task.WorkingDirectory)
                        ? task.WorkingDirectory
                        : null
                );
                started++;
            }
            catch (Exception exception)
            {
                failures.Add($"{task.Title}: {exception.Message}");
            }
        }
        return Task.FromResult(new CodexTaskRecoveryResult(tasks.Count, started, failures));
    }

    private sealed class AppServerProbe : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));
        private int _nextRequestId = 1;

        private AppServerProbe(Process process) => _process = process;

        public static async Task<AppServerProbe> StartAsync(string codexHome, string executable)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add("app-server");
            info.Environment["CODEX_HOME"] = codexHome;
            var process = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start Codex app-server.");
            _ = process.StandardError.ReadToEndAsync();
            var probe = new AppServerProbe(process);
            await probe.RequestAsync("initialize", new
            {
                clientInfo = new
                {
                    name = "codex_account_bar",
                    title = "Codex Account Bar",
                    version = "2.2.0"
                },
                capabilities = new { experimentalApi = true }
            });
            await probe.SendAsync(new { method = "initialized", @params = new { } });
            return probe;
        }

        public async Task<IReadOnlyList<RecoverableCodexTask>> FindUsageLimitedTasksAsync(
            DateTimeOffset since,
            int maximumCount
        )
        {
            var list = await RequestAsync("thread/list", new
            {
                limit = 30,
                sortKey = "updated_at",
                sortDirection = "desc",
                sourceKinds = new[] { "vscode", "cli", "appServer", "exec" }
            });
            var cutoff = since.ToUnixTimeSeconds();
            var rows = list["data"]?.AsArray() ?? [];
            var tasks = new List<RecoverableCodexTask>();
            foreach (var node in rows)
            {
                if (tasks.Count >= maximumCount || node is not JsonObject row) break;
                if ((row["updatedAt"]?.GetValue<long>() ?? 0) < cutoff) continue;
                if (row["parentThreadId"] is not null) continue;
                var threadId = row["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(threadId)) continue;

                var turnsPage = await RequestAsync("thread/turns/list", new
                {
                    threadId,
                    limit = 1,
                    sortDirection = "desc",
                    itemsView = "summary"
                });
                var turns = turnsPage["data"]?.AsArray();
                if (turns is null || turns.Count == 0 || turns[^1] is not JsonObject lastTurn) continue;
                var errorInfo = lastTurn["error"]?["codexErrorInfo"]?.GetValue<string>();
                if (lastTurn["status"]?.GetValue<string>() != "failed"
                    || errorInfo != "usageLimitExceeded") continue;

                var title = FirstNonEmpty(
                    row["name"]?.GetValue<string>(),
                    row["preview"]?.GetValue<string>(),
                    threadId
                );
                if (title.Length > 80) title = title[..80];
                tasks.Add(new RecoverableCodexTask(
                    threadId,
                    title,
                    row["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory
                ));
            }
            return tasks;
        }

        private async Task<JsonObject> RequestAsync(string method, object parameters)
        {
            var id = _nextRequestId++;
            await SendAsync(new { method, id, @params = parameters });
            while (await _process.StandardOutput.ReadLineAsync(_deadline.Token) is { } line)
            {
                var message = JsonNode.Parse(line)?.AsObject();
                if (message?["id"]?.GetValue<int>() != id) continue;
                if (message["error"] is JsonObject error)
                {
                    throw new InvalidOperationException(
                        error["message"]?.GetValue<string>() ?? "Unknown Codex app-server error."
                    );
                }
                return message["result"]?.AsObject()
                    ?? throw new InvalidDataException($"Codex returned an invalid response for {method}.");
            }
            throw new EndOfStreamException("Codex app-server stopped unexpectedly.");
        }

        private async Task SendAsync(object message)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await _process.StandardInput.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { _process.StandardInput.Close(); } catch { }
            if (!_process.HasExited)
            {
                try { _process.Kill(true); } catch { }
                try { await _process.WaitForExitAsync(); } catch { }
            }
            _process.Dispose();
            _deadline.Dispose();
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.First(value => !string.IsNullOrWhiteSpace(value))!;
    }
}
