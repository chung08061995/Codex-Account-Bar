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
    private readonly DispatcherTimer _failoverTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private bool _routerRunning;
    private bool _routerCanToggle;
    private bool _routerBusy;
    private bool _refreshingUsage;
    private bool _autoFailoverEnabled = AppSettingsStore.ReadAutoFailoverEnabled();
    private bool _isAutoRecovering;
    private bool _forceClose;
    private DateTimeOffset? _lastAutoFailoverAt;
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
    public bool AutoFailoverEnabled
    {
        get => _autoFailoverEnabled;
        set
        {
            if (!Set(ref _autoFailoverEnabled, value)) return;
            AppSettingsStore.WriteAutoFailoverEnabled(value);
        }
    }
    public bool IsAutoRecovering { get => _isAutoRecovering; private set => Set(ref _isAutoRecovering, value); }
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
        _failoverTimer.Tick += async (_, _) =>
        {
            _failoverTimer.Stop();
            try { await MonitorActiveQuotaAsync(); }
            finally { if (!_forceClose) _failoverTimer.Start(); }
        };
        _failoverTimer.Start();
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
                var usesActiveAuth = false;
                try
                {
                    usesActiveAuth = activeAuth is not null
                        && account.Email.Equals(activeIdentity?.Email, StringComparison.OrdinalIgnoreCase);
                    var auth = usesActiveAuth ? activeAuth! : await _vault.ReadAuthAsync(account.Id);
                    var refreshed = await _usage.FetchRefreshingCredentialAsync(auth);
                    await CommitRefreshedCredentialAsync(account, auth, refreshed.AuthJson);
                    ApplyUsage(account, refreshed.Usage);
                }
                catch (Exception e)
                {
                    succeeded = false;
                    if (IsCredentialFailure(e)) MarkCredentialFailure(account);
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
        if (account.RequiresSignIn)
        {
            await ReauthenticateAccountAsync(account);
            return;
        }
        Message = $"Switching to {account.Email}…";
        try
        {
            await PersistActiveCredentialToVaultAsync();
            var savedAuth = await _vault.ReadAuthAsync(account.Id);
            var refreshedCredential = await _usage.FetchRefreshingCredentialAsync(savedAuth);
            if (!string.Equals(refreshedCredential.AuthJson, savedAuth, StringComparison.Ordinal))
            {
                await _vault.SaveAsync(refreshedCredential.AuthJson);
            }
            ApplyUsage(account, refreshedCredential.Usage);
            account.NotifyAll();
            await _codex.WriteAndRestartAsync(refreshedCredential.AuthJson);
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
            if (IsCredentialFailure(exception))
            {
                MarkCredentialFailure(account);
                account.NotifyAll();
                Message = $"{account.Email} needs sign-in before it can be switched.";
            }
            else
            {
                Message = exception.Message;
            }
        }
    }

    private async Task ReauthenticateAccountAsync(AccountRecord account)
    {
        Message = $"Sign in to {account.Email} in your browser…";
        try
        {
            string auth;
            try
            {
                auth = await _codex.LoginIsolatedAsync();
            }
            catch (FileNotFoundException)
            {
                throw new InvalidOperationException("Codex CLI is required to sign this account in again.");
            }

            var identity = AuthInspector.Inspect(auth);
            if (!account.Email.Equals(identity.Email, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Sign in to {account.Email}, not {identity.Email}.");

            await _vault.SaveAsync(auth);
            var refreshed = await _usage.FetchRefreshingCredentialAsync(auth);
            await CommitRefreshedCredentialAsync(account, auth, refreshed.AuthJson);
            ApplyUsage(account, refreshed.Usage);
            account.NotifyAll();
            Message = $"Signed in to {account.Email}. You can switch to it now.";
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

    private async Task MonitorActiveQuotaAsync()
    {
        if (!AutoFailoverEnabled || _refreshingUsage || IsAutoRecovering) return;
        var active = Accounts.FirstOrDefault(account => account.IsActive);
        if (active is null) return;
        try
        {
            var activeAuth = await _codex.ReadActiveAuthAsync();
            var auth = activeAuth is not null
                && active.Email.Equals(AuthInspector.Inspect(activeAuth).Email, StringComparison.OrdinalIgnoreCase)
                ? activeAuth
                : await _vault.ReadAuthAsync(active.Id);
            var refreshed = await _usage.FetchRefreshingCredentialAsync(auth);
            await CommitRefreshedCredentialAsync(active, auth, refreshed.AuthJson);
            ApplyUsage(active, refreshed.Usage);
            active.NotifyAll();
            if (QuotaFailoverPolicy.IsExhausted(active))
                await AttemptAutoFailoverAsync(active);
        }
        catch (Exception exception)
        {
            if (IsCredentialFailure(exception))
            {
                MarkCredentialFailure(active);
                active.NotifyAll();
            }
            AppLog.Error("Automatic quota check", exception);
            Message = "Auto-switch check failed; retrying.";
        }
    }

    private async Task AttemptAutoFailoverAsync(AccountRecord exhaustedAccount)
    {
        if (!AutoFailoverEnabled || IsAutoRecovering) return;
        if (_lastAutoFailoverAt is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(10)) return;

        IsAutoRecovering = true;
        Message = "Quota exhausted; checking other accounts…";
        AccountRecord? selectedCandidate = null;
        try
        {
            await PersistActiveCredentialToVaultAsync();
            await RefreshAllAsync();
            if (!QuotaFailoverPolicy.IsExhausted(exhaustedAccount))
            {
                Message = "Quota restored; staying on the current account.";
                return;
            }
            var candidate = QuotaFailoverPolicy.BestCandidate(Accounts, exhaustedAccount.Id);
            if (candidate is null)
            {
                Message = "Quota exhausted; no other account has usable quota.";
                return;
            }
            selectedCandidate = candidate;

            var savedAuth = await _vault.ReadAuthAsync(candidate.Id);
            var refreshed = await _usage.FetchRefreshingCredentialAsync(savedAuth);
            if (!string.Equals(refreshed.AuthJson, savedAuth, StringComparison.Ordinal))
                await _vault.SaveAsync(refreshed.AuthJson);
            ApplyUsage(candidate, refreshed.Usage);
            candidate.NotifyAll();

            var cli = _codex.FindCodexCli();
            IReadOnlyList<RecoverableCodexTask> tasks = [];
            CodexTaskRecoveryService? recovery = null;
            string? taskScanError = null;
            if (cli is not null)
            {
                recovery = new CodexTaskRecoveryService(_codex.CodexHome, cli);
                Message = "Finding tasks stopped by quota…";
                try { tasks = await recovery.FindUsageLimitedTasksAsync(); }
                catch (Exception exception)
                {
                    taskScanError = exception.Message;
                    AppLog.Error("Find quota-limited tasks", exception);
                }
            }

            var launchTarget = await _codex.TerminateAsync();
            try
            {
                await _router.EnsureStoppedAsync();
                await _codex.RestoreOfficialConfigAsync();
                await _codex.WriteAuthAsync(refreshed.AuthJson);
                foreach (var account in Accounts)
                {
                    account.IsActive = account.Id == candidate.Id;
                    account.NotifyAll();
                }
                _lastAutoFailoverAt = DateTimeOffset.UtcNow;

                var result = new CodexTaskRecoveryResult(0, 0, []);
                if (recovery is not null && tasks.Count > 0)
                {
                    Message = $"Starting {tasks.Count} stopped task(s) on {candidate.Email}…";
                    result = await recovery.ResumeAsync(tasks);
                }
                if (!await _codex.LaunchAsync(launchTarget))
                    throw new InvalidOperationException("Codex Desktop could not be reopened.");
                await RefreshAllAsync();
                Message = taskScanError is not null
                    ? $"Switched to {candidate.Email}, but task recovery scan failed: {taskScanError}"
                    : cli is null
                    ? $"Switched to {candidate.Email}. Codex CLI was not found, so stopped tasks need manual continuation."
                    : result.Failures.Count == 0
                        ? tasks.Count == 0
                            ? $"Switched to {candidate.Email}."
                            : $"Switched account; started {result.Started} task(s)."
                        : $"Switched account; started {result.Started}/{result.Attempted} task(s).";
                if (result.Failures.Count > 0) AppLog.Error("Resume quota-limited tasks", new AggregateException(result.Failures.Select(message => new Exception(message))));
            }
            catch
            {
                await _codex.LaunchAsync(launchTarget);
                throw;
            }
        }
        catch (Exception exception)
        {
            if (selectedCandidate is not null && IsCredentialFailure(exception))
            {
                MarkCredentialFailure(selectedCandidate);
                selectedCandidate.NotifyAll();
            }
            AppLog.Error("Automatic account failover", exception);
            Message = "Automatic account switch failed: " + exception.Message;
        }
        finally { IsAutoRecovering = false; }
    }

    private static void ApplyUsage(AccountRecord account, UsageResult usage)
    {
        account.HasUsage = true;
        account.RequiresSignIn = false;
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

    private async Task PersistActiveCredentialToVaultAsync()
    {
        var activeAuth = await _codex.ReadActiveAuthAsync();
        if (activeAuth is null) return;
        var identity = AuthInspector.Inspect(activeAuth);
        if (Accounts.Any(account => account.Email.Equals(
            identity.Email,
            StringComparison.OrdinalIgnoreCase
        )))
        {
            await _vault.SaveAsync(activeAuth);
        }
    }

    private async Task CommitRefreshedCredentialAsync(
        AccountRecord account,
        string sourceAuth,
        string refreshedAuth
    )
    {
        var latestActive = await _codex.ReadActiveAuthAsync();
        var accountIsActive = latestActive is not null
            && account.Email.Equals(
                AuthInspector.Inspect(latestActive).Email,
                StringComparison.OrdinalIgnoreCase
            );

        string savedAuth;
        if (!accountIsActive)
        {
            savedAuth = refreshedAuth;
        }
        else if (!string.Equals(latestActive, sourceAuth, StringComparison.Ordinal))
        {
            // Codex refreshed concurrently; its newer credential wins.
            savedAuth = latestActive!;
        }
        else
        {
            // Update the live Codex auth before the vault so a crash cannot leave
            // Codex holding a refresh token that Bar already rotated.
            await _codex.WriteAuthAsync(refreshedAuth);
            savedAuth = refreshedAuth;
        }
        await _vault.SaveAsync(savedAuth);
    }

    private static bool IsCredentialFailure(Exception exception) =>
        exception is System.Net.Http.HttpRequestException
        { StatusCode: System.Net.HttpStatusCode.Unauthorized };

    private static void MarkCredentialFailure(AccountRecord account)
    {
        account.HasUsage = false;
        account.RequiresSignIn = true;
        account.StatusText = "Session expired. Sign in again.";
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Message = $"Codex auth: {_codex.AuthPath}\n9Router service, Docker and npm CLI detection enabled.";

    public void ForceClose()
    {
        _refreshTimer.Stop();
        _failoverTimer.Stop();
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

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
