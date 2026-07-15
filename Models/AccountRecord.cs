using System.ComponentModel;
namespace CodexAccountBar.Models;
public sealed class AccountRecord : INotifyPropertyChanged
{
    public required string Id { get; init; } public required string Email { get; init; } public string Plan { get; set; } = "ChatGPT"; public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } public double SessionUsed { get; set; } public double WeeklyUsed { get; set; } public string SessionReset { get; set; } = "Reset unknown"; public string WeeklyReset { get; set; } = "Reset unknown"; public string StatusText { get; set; } = "Refresh to load usage";
    public double SessionLeft => Math.Max(0, 100 - SessionUsed); public double WeeklyLeft => Math.Max(0, 100 - WeeklyUsed);
    public event PropertyChangedEventHandler? PropertyChanged; public void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
public sealed record AccountIndexItem(string Id, string Email, string Plan, DateTimeOffset AddedAt);
