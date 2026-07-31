using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAccountBar.Models;
using CodexAccountBar.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexAccountBar;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumRetryInterval = TimeSpan.FromMinutes(30);

    private readonly AccountVault _vault = new();
    private readonly CodexService _codex = new();
    private readonly UsageService _usage = new();
    private readonly NineRouterService _router = new();
    private readonly DispatcherTimer _refreshTimer = new();

    private bool _routerRunning;
    private bool _routerCanToggle;
    private bool _routerBusy;
    private bool _refreshingUsage;
    private bool _forceClose;
    private TimeSpan _nextRefreshDelay = AutomaticRefreshInterval;
    private string _routerDetail = "Detecting…";
    private string _message = "";

    public ObservableCollection<AccountRecord> Accounts { get; } = [];
    public bool RouterRunning { get => _routerRunning; private set => Set(ref _routerRunning, value); }
    public bool RouterCanToggle => _routerCanToggle && !_routerBusy;
    public string RouterDetail { get => _routerDetail; private set => Set(ref _routerDetail, value); }
    public string Message
    {
        get => _message;
        private set
        {
            Set(ref _message, value);
            Changed(nameof(MessageVisibility));
        }
    }
    public Visibility MessageVisibility => string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            await RefreshAllAsync();
        };
        Loaded += async (_, _) =>
        {
            if (Accounts.Count == 0)
            {
                await Load();
            }
        };
    }

    private async Task Load()
    {
        try
        {
            RouterDetail = "Restoring official Codex configuration…";
            try
            {
                await _router.EnsureStoppedAsync();
                await _codex.RestoreOfficialConfigAsync();
            }
            catch (Exception e)
            {
                AppLog.Error("Restore official configuration", e);
                Message = "Could not fully disable 9Router: " + e.Message;
            }

            Accounts.Clear();
            foreach (var account in await _vault.LoadAsync())
            {
                Accounts.Add(account);
            }

            var active = await _codex.ReadActiveAuthAsync();
            if (active is not null)
            {
                var identity = AuthInspector.Inspect(active);
                foreach (var account in Accounts)
                {
                    account.IsActive = account.Email.Equals(identity.Email, StringComparison.OrdinalIgnoreCase);
                }
                if (Accounts.Any(x => x.IsActive))
                {
                    await _vault.SaveAsync(active);
                }
                if (!Accounts.Any(x => x.IsActive))
                {
                    var added = await _vault.SaveAsync(active);
                    added.IsActive = true;
                    Accounts.Add(added);
                }
            }

            Changed(nameof(EmptyVisibility));
            await RefreshAllAsync();
        }
        catch (Exception e)
        {
            Message = e.Message;
            ScheduleNextRefresh(succeeded: false);
        }
    }

    public async Task<bool> RefreshAllAsync()
    {
        if (_refreshingUsage)
        {
            return false;
        }

        _refreshingUsage = true;
        Message = "";
        var succeeded = true;
        try
        {
            try
            {
                await RefreshRouter();
            }
            catch (Exception e)
            {
                succeeded = false;
                AppLog.Error("Refresh router status", e);
            }

            var activeAuth = await _codex.ReadActiveAuthAsync();
            var activeIdentity = activeAuth is null ? null : AuthInspector.Inspect(activeAuth);
            foreach (var account in Accounts)
            {
                try
                {
                    var usesActiveAuth = activeAuth is not null
                        && account.Email.Equals(activeIdentity?.Email, StringComparison.OrdinalIgnoreCase);
                    var auth = usesActiveAuth ? activeAuth! : await _vault.ReadAuthAsync(account.Id);
                    if (usesActiveAuth)
                    {
                        await _vault.SaveAsync(auth);
                    }
                    var usage = await _usage.FetchAsync(auth);
                    account.PrimaryTitle = usage.Primary.Title;
                    account.PrimaryUsed = usage.Primary.UsedPercent;
                    account.PrimaryReset = usage.Primary.ResetText;
                    account.HasSecondary = usage.Secondary is not null;
                    if (usage.Secondary is not null)
                    {
                        account.SecondaryTitle = usage.Secondary.Title;
                        account.SecondaryUsed = usage.Secondary.UsedPercent;
                        account.SecondaryReset = usage.Secondary.ResetText;
                    }
                    account.AvailableResetCount = usage.AvailableResetCount;
                    account.StatusText = usage.AutomaticResetApplied
                        ? "Quota exhausted; available reset applied"
                        : "Usage refreshed";
                }
                catch (Exception e)
                {
                    succeeded = false;
                    account.StatusText = e.Message;
                    AppLog.Error($"Refresh usage for {account.Email}", e);
                }
                account.NotifyAll();
            }

            return succeeded;
        }
        finally
        {
            _refreshingUsage = false;
            ScheduleNextRefresh(succeeded);
        }
    }

    private void ScheduleNextRefresh(bool succeeded)
    {
        _refreshTimer.Stop();
        if (succeeded)
        {
            _nextRefreshDelay = AutomaticRefreshInterval;
        }
        else if (_nextRefreshDelay == AutomaticRefreshInterval)
        {
            _nextRefreshDelay = InitialRetryInterval;
        }
        else
        {
            _nextRefreshDelay = TimeSpan.FromMinutes(Math.Min(
                _nextRefreshDelay.TotalMinutes * 2,
                MaximumRetryInterval.TotalMinutes
            ));
        }
        _refreshTimer.Interval = _nextRefreshDelay;
        _refreshTimer.Start();
    }

    private async Task RefreshRouter()
    {
        var status = await _router.DetectAsync();
        RouterRunning = status.Running;
        _routerCanToggle = status.Installed;
        RouterDetail = status.Detail;
        Changed(nameof(RouterCanToggle));
    }

    private async void RouterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_routerBusy) return;
        var requested = (sender as WpfCheckBox)?.IsChecked == true;
        _routerBusy = true;
        Changed(nameof(RouterCanToggle));
        RouterDetail = requested ? "Starting 9Router…" : "Stopping 9Router…";
        try
        {
            var next = await _router.SetRunningAsync(requested);
            RouterRunning = next.Running;
            RouterDetail = next.Detail;
        }
        catch (Exception exception)
        {
            Message = exception.Message;
            await RefreshRouter();
        }
        finally
        {
            _routerBusy = false;
            Changed(nameof(RouterCanToggle));
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        Message = "Complete Codex sign-in in your browser…";
        try
        {
            string auth;
            try
            {
                auth = await _codex.LoginIsolatedAsync();
            }
            catch (FileNotFoundException)
            {
                auth = await _codex.ReadActiveAuthAsync()
                    ?? throw new InvalidOperationException("Codex CLI is not installed and no active Codex account was found. Install the Codex CLI to add a new account.");
                Message = "Codex CLI not found; importing the account currently signed in to Codex…";
            }

            var account = await _vault.SaveAsync(auth);
            if (!Accounts.Any(x => x.Id == account.Id))
            {
                Accounts.Add(account);
            }
            Changed(nameof(EmptyVisibility));
            var refreshed = await RefreshAllAsync();
            Message = refreshed
                ? $"Saved {account.Email} securely."
                : $"Saved {account.Email} securely. Quota refresh will retry automatically.";
        }
        catch (Exception exception)
        {
            Message = exception.Message;
        }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfButton)?.Tag is not AccountRecord account) return;
        Message = $"Switching to {account.Email}…";
        try
        {
            await _codex.WriteAndRestartAsync(await _vault.ReadAuthAsync(account.Id));
            foreach (var item in Accounts)
            {
                item.IsActive = item.Id == account.Id;
                item.NotifyAll();
            }
            var refreshed = await RefreshAllAsync();
            Message = refreshed
                ? $"Switched to {account.Email}. Codex Desktop restarted."
                : $"Switched to {account.Email}. Quota refresh will retry automatically.";
        }
        catch (Exception exception)
        {
            Message = exception.Message;
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfButton)?.Tag is not AccountRecord account) return;
        if (WpfMessageBox.Show(
            $"Remove {account.Email} from this app?\n\nThis does not sign the account out of Codex.",
            "Codex Account Bar",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        ) != MessageBoxResult.Yes)
        {
            return;
        }

        await _vault.RemoveAsync(account.Id);
        Accounts.Remove(account);
        Changed(nameof(EmptyVisibility));
        Message = $"Removed {account.Email} from Codex Account Bar.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Message = "Refreshing accounts and usage…";
        var succeeded = await RefreshAllAsync();
        Message = succeeded ? "Refresh complete." : "Refresh completed with errors. Retrying automatically.";
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Message = $"Codex auth: {_codex.AuthPath}\n9Router service, Docker and npm CLI detection enabled.";

    public void ForceClose()
    {
        _refreshTimer.Stop();
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
