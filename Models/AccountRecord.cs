using System.ComponentModel;
using System.Globalization;
namespace CodexAccountBar.Models;
public sealed class AccountRecord : INotifyPropertyChanged
{
    #region Properties

    public required string Id { get; init; }
    public required string Email { get; init; }
    public string Plan { get; set; } = "ChatGPT";
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; }
    public double? SessionUsed { get; set; }
    public double? WeeklyUsed { get; set; }
    public DateTimeOffset? SessionResetAt { get; set; }
    public DateTimeOffset? WeeklyResetAt { get; set; }
    public string SessionTitle { get; set; } = "Session";
    public string WeeklyTitle { get; set; } = "Weekly";
    public string StatusText { get; set; } = "Refresh to load quota";
    public double? SessionLeft => SessionUsed is { } used ? Math.Clamp(100 - used, 0, 100) : null;
    public double? WeeklyLeft => WeeklyUsed is { } used ? Math.Clamp(100 - used, 0, 100) : null;
    public double SessionProgress => SessionLeft ?? 0;
    public double WeeklyProgress => WeeklyLeft ?? 0;
    public string SessionQuotaText => QuotaText(SessionLeft);
    public string WeeklyQuotaText => QuotaText(WeeklyLeft);
    public string SessionReset => ResetText(SessionResetAt, SessionUsed.HasValue);
    public string WeeklyReset => ResetText(WeeklyResetAt, WeeklyUsed.HasValue);

    #endregion

    #region Events

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    #region Public Methods

    public void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    #endregion

    #region Private Methods

    private static string QuotaText(double? left) => left is { } value ? $"{value.ToString("0.#", CultureInfo.CurrentCulture)}% left" : "Unavailable";

    private static string ResetText(DateTimeOffset? resetAt, bool available)
    {
        if (!available) return "Quota not available";
        if (resetAt is null) return "Reset time unavailable";
        var remaining = resetAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "Reset due; refresh to update";
        var minutes = (long)Math.Ceiling(remaining.TotalMinutes);
        var duration = minutes >= 1440 ? $"{minutes / 1440}d {minutes % 1440 / 60}h {minutes % 60}m" : $"{minutes / 60}h {minutes % 60}m";
        return $"Resets in {duration} · {resetAt.Value.ToLocalTime():ddd, dd MMM HH:mm}";
    }

    #endregion
}
public sealed record AccountIndexItem(string Id, string Email, string Plan, DateTimeOffset AddedAt);
