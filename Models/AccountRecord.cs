using System.ComponentModel;
using System.Windows;

namespace CodexAccountBar.Models;

public sealed class AccountRecord : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public string Plan { get; set; } = "ChatGPT";
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; }
    public bool HasUsage { get; set; }
    public bool RequiresSignIn { get; set; }

    public string PrimaryActionText => RequiresSignIn || !HasUsage ? "Sign In" : IsActive ? "Active" : "Switch";
    public bool CanPrimaryAction => RequiresSignIn || !HasUsage || !IsActive;
    public Visibility UsageVisibility => HasUsage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingUsageVisibility => HasUsage ? Visibility.Collapsed : Visibility.Visible;
    public string MissingUsageText => RequiresSignIn
        ? "Session expired. Sign in again to load quota."
        : "Sign in to verify this saved session and load quota.";

    public string PrimaryTitle { get; set; } = "Quota";
    public double PrimaryUsed { get; set; }
    public string PrimaryReset { get; set; } = "Refresh to load reset time";
    public double PrimaryLeft => Math.Max(0, 100 - PrimaryUsed);

    public bool HasSecondary { get; set; }
    public string SecondaryTitle { get; set; } = "Quota";
    public double SecondaryUsed { get; set; }
    public string SecondaryReset { get; set; } = "Reset unknown";
    public double SecondaryLeft => Math.Max(0, 100 - SecondaryUsed);

    public int AvailableResetCount { get; set; }
    public bool HasAvailableResets => AvailableResetCount > 0;
    public string AvailableResetText => $"{AvailableResetCount} rate-limit reset{(AvailableResetCount == 1 ? "" : "s")} available";

    public string StatusText { get; set; } = "Refresh to load usage";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed record AccountIndexItem(string Id, string Email, string Plan, DateTimeOffset AddedAt);
