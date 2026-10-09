using System.Globalization;
using System.Text.RegularExpressions;
using CodexAccountBar.Models;

namespace CodexAccountBar.Services;

public sealed class RequestLogService
{
    #region Constants

    public const int MaximumRows = 500;
    public const string UnknownAccount = "Not recorded";

    #endregion

    #region Public Methods

    public Task<IReadOnlyList<RequestLogRecord>> ReadAsync(string codexHome, IReadOnlyDictionary<string, string> accounts, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<RequestLogRecord>>(() => Read(codexHome, accounts, cancellationToken), cancellationToken);

    #endregion

    #region Private Methods

    private static List<RequestLogRecord> Read(string home, IReadOnlyDictionary<string, string> accounts, CancellationToken cancellationToken)
    {
        var statePath = DatabasePath(home, "state");
        var logsPath = DatabasePath(home, "logs");
        using var state = new LocalSqlite(statePath);
        using var logs = new LocalSqlite(logsPath);
        var columns = state.Query("PRAGMA table_info(threads)").Select(row => row[1]).ToHashSet();
        var accountColumn = columns.Contains("creator_account_id") ? "creator_account_id" : "NULL";
        var nameColumn = columns.Contains("name") ? "COALESCE(NULLIF(name, ''), title)" : "title";
        var sessions = state.Query($"SELECT id, {nameColumn}, {accountColumn} FROM threads").ToDictionary(row => row[0]!);
        var since = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var records = new List<RequestLogRecord>();
        var rows = logs.Query($"SELECT ts, ts_nanos, substr(feedback_log_body, 1, CASE WHEN instr(feedback_log_body, ' headers=') > 0 THEN instr(feedback_log_body, ' headers=') - 1 ELSE 4096 END), thread_id FROM logs WHERE ts >= CAST(? AS INTEGER) AND target = 'codex_http_client::client' AND feedback_log_body LIKE '%Request completed method=POST url=%/responses status=%' ORDER BY ts DESC, ts_nanos DESC, id DESC LIMIT {MaximumRows}", since);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = row[3] ?? "Unavailable";
            sessions.TryGetValue(sessionId, out var session);
            var accountId = session?[2];
            var creator = accountId is not null && accounts.TryGetValue(accountId, out var email) ? email : UnknownAccount;
            var title = session is null ? "Session name unavailable" : string.IsNullOrWhiteSpace(session[1]) ? $"Unnamed session ({sessionId})" : session[1]!;
            records.Add(Parse(row, title, sessionId, creator));
        }
        return records;
    }

    private static RequestLogRecord Parse(string?[] row, string title, string sessionId, string creator)
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(row[0]!, CultureInfo.InvariantCulture))
            .AddTicks(long.Parse(row[1]!, CultureInfo.InvariantCulture) / 100);
        var body = row[2] ?? "";
        var model = Regex.Match(body, @"\bmodel=""?([\w.\-/]+)", RegexOptions.CultureInvariant).Groups[1].Value;
        var status = Regex.Match(body, @"\bstatus=(\d{3})\b", RegexOptions.CultureInvariant).Groups[1].Value;
        return new(timestamp, UnknownAccount, title, sessionId, model.Length == 0 ? "Unavailable" : model, status.Length == 0 ? "Unavailable" : $"HTTP {status}", creator);
    }

    private static string DatabasePath(string home, string prefix)
    {
        if (!Directory.Exists(home)) throw new FileNotFoundException("The local Codex home was not found.");
        return Directory.EnumerateFiles(home, $"{prefix}_*.sqlite")
            .Select(path => new { Path = path, Version = int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(prefix.Length + 1), out var version) ? version : -1 })
            .Where(item => item.Version >= 0).OrderByDescending(item => item.Version).FirstOrDefault()?.Path
            ?? throw new FileNotFoundException($"The local Codex {prefix} database was not found. Open a Codex session first.");
    }

    #endregion
}
