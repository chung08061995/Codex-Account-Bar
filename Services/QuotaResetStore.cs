using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record QuotaResetState(int SessionCount, int WeeklyCount, DateTimeOffset? SessionResetAt, DateTimeOffset? WeeklyResetAt, string SessionTitle, string WeeklyTitle, DateTimeOffset TrackingStartedAt);

public sealed class QuotaResetStore
{
    #region Fields

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    #endregion

    #region Initialization

    public QuotaResetStore(string? path = null) => _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountBar", "quota-resets.json");

    #endregion

    #region Public Methods

    public async Task<QuotaResetState?> ReadAsync(string accountId)
    {
        await _gate.WaitAsync();
        try { return (await LoadAsync()).GetValueOrDefault(accountId); }
        finally { _gate.Release(); }
    }

    public async Task<QuotaResetState> ObserveAsync(string accountId, UsageResult usage, DateTimeOffset fetchedAt)
    {
        await _gate.WaitAsync();
        try
        {
            var states = await LoadAsync();
            var previous = states.GetValueOrDefault(accountId);
            var next = Observe(previous, usage, fetchedAt);
            states[accountId] = next;
            if (next != previous) await SaveAsync(states);
            return next;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string accountId)
    {
        await _gate.WaitAsync();
        try
        {
            var states = await LoadAsync();
            if (states.Remove(accountId)) await SaveAsync(states);
        }
        finally { _gate.Release(); }
    }

    public static QuotaResetState Observe(QuotaResetState? previous, UsageResult usage, DateTimeOffset fetchedAt)
    {
        previous ??= new(0, 0, null, null, usage.SessionTitle, usage.WeeklyTitle, fetchedAt);
        var sessionReset = usage.SessionUsed.HasValue ? usage.SessionResetAt : null;
        var weeklyReset = usage.WeeklyUsed.HasValue ? usage.WeeklyResetAt : null;
        var sessionCount = previous.SessionCount + (RolledOver(previous.SessionResetAt, sessionReset, previous.SessionTitle, usage.SessionTitle, fetchedAt) ? 1 : 0);
        var weeklyCount = previous.WeeklyCount + (RolledOver(previous.WeeklyResetAt, weeklyReset, previous.WeeklyTitle, usage.WeeklyTitle, fetchedAt) ? 1 : 0);
        return new(sessionCount, weeklyCount, LatestReset(previous.SessionResetAt, sessionReset, previous.SessionTitle, usage.SessionTitle), LatestReset(previous.WeeklyResetAt, weeklyReset, previous.WeeklyTitle, usage.WeeklyTitle),
            sessionReset.HasValue ? usage.SessionTitle : previous.SessionTitle,
            weeklyReset.HasValue ? usage.WeeklyTitle : previous.WeeklyTitle, previous.TrackingStartedAt);
    }

    #endregion

    #region Private Methods

    private static DateTimeOffset? LatestReset(DateTimeOffset? previous, DateTimeOffset? current, string previousTitle, string currentTitle) => current is null || (previousTitle == currentTitle && previous > current) ? previous : current;

    private static bool RolledOver(DateTimeOffset? previous, DateTimeOffset? current, string previousTitle, string currentTitle, DateTimeOffset fetchedAt) => previous.HasValue && current.HasValue && previousTitle == currentTitle && previous.Value <= fetchedAt && current.Value > previous.Value && current.Value > fetchedAt;

    private async Task<Dictionary<string, QuotaResetState>> LoadAsync() => File.Exists(_path)
        ? JsonSerializer.Deserialize<Dictionary<string, QuotaResetState>>(await File.ReadAllTextAsync(_path)) ?? []
        : [];

    private async Task SaveAsync(Dictionary<string, QuotaResetState> states)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(states));
        File.Move(temporary, _path, true);
    }

    #endregion
}
