using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using CodexAccountBar.Models;
using CodexAccountBar.Services;

namespace CodexAccountBar;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    #region Fields

    private readonly AccountVault _vault = new();
    private readonly CodexService _codex = new();
    private readonly UsageService _usage = new();
    private readonly QuotaResetStore _resetStore = new();
    private readonly SemaphoreSlim _quotaGate = new(1, 1);
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _forceClose;
    private bool _loaded;
    private bool _refreshBusy;
    private bool _isAdding;
    private CancellationTokenSource? _loginCancellation;
    private string _message = "";

    #endregion

    #region Properties

    public ObservableCollection<AccountRecord> Accounts { get; } = [];
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
    public bool CanAddAccount => !_isAdding;
    public bool CanCancelSignIn => _loginCancellation is { IsCancellationRequested: false };
    public Visibility AddVisibility => _loginCancellation is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => _loginCancellation is null ? Visibility.Collapsed : Visibility.Visible;

    #endregion

    #region Events

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    #region Framework Lifecycle

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += Window_Loaded;
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _refreshTimer.Stop();
            _loginCancellation?.Cancel();
        }
        base.OnClosing(e);
    }

    #endregion

    #region Initialization

    private async Task LoadAsync()
    {
        try
        {
            foreach (var account in await _vault.LoadAsync())
            {
                ApplyResetState(account, await _resetStore.ReadAsync(account.Id));
                Accounts.Add(account);
            }
            var active = await _codex.ReadActiveAuthAsync();
            if (active is not null)
            {
                var identity = AuthInspector.Inspect(active);
                foreach (var account in Accounts) account.IsActive = account.Email.Equals(identity.Email, StringComparison.OrdinalIgnoreCase);
                if (!Accounts.Any(account => account.IsActive))
                {
                    var added = await _vault.SaveAsync(active);
                    added.IsActive = true;
                    Accounts.Add(added);
                }
            }
            Changed(nameof(EmptyVisibility));
            await RefreshAllAsync();
        }
        catch (Exception exception) { Message = exception.Message; }
    }

    #endregion

    #region Public Methods

    public async Task RefreshAllAsync()
    {
        if (_refreshBusy) return;
        _refreshBusy = true;
        try
        {
            foreach (var account in Accounts.ToArray()) await RefreshAccountAsync(account);
        }
        finally { _refreshBusy = false; }
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    #endregion

    #region Event Handlers

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshAllAsync();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_isAdding) return;
        _isAdding = true;
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        NotifySignInState();
        Message = "Complete Codex sign-in in your browser…";
        try
        {
            var auth = await _codex.LoginIsolatedAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _loginCancellation = null;
            NotifySignInState();
            var saved = await _vault.SaveAsync(auth);
            var account = Accounts.FirstOrDefault(item => item.Id == saved.Id);
            if (account is null)
            {
                account = saved;
                Accounts.Add(account);
            }
            Changed(nameof(EmptyVisibility));
            await RefreshAccountAsync(account);
            Message = $"Saved {account.Email} securely.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Message = "Sign-in cancelled. No account was added."; }
        catch (Exception exception) { Message = exception.Message; }
        finally
        {
            _loginCancellation = null;
            _isAdding = false;
            NotifySignInState();
        }
    }

    private void CancelSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCancelSignIn) return;
        Message = "Cancelling sign-in…";
        _loginCancellation?.Cancel();
        NotifySignInState();
    }

    private async void CheckResets_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not AccountRecord account || _refreshBusy) return;
        _refreshBusy = true;
        try { await RefreshAccountAsync(account); }
        finally { _refreshBusy = false; }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not AccountRecord account) return;
        if (System.Windows.MessageBox.Show($"Remove {account.Email} from this app?", "Codex Account Bar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _vault.RemoveAsync(account.Id);
            Accounts.Remove(account);
            Changed(nameof(EmptyVisibility));
            await _resetStore.RemoveAsync(account.Id);
        }
        catch (Exception exception) { Message = exception.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Message = "Refreshing accounts and quota…";
        await RefreshAllAsync();
        Message = Accounts.Any(account => !account.SessionUsed.HasValue && !account.WeeklyUsed.HasValue)
            ? "Some quotas are unavailable. See the status below each account."
            : "Refresh complete.";
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Settings_Click(object sender, RoutedEventArgs e) => Message = $"Codex auth: {_codex.AuthPath}\nQuota refreshes every minute. Reset counts track observed OpenAI resets since tracking started; earlier history is unavailable.";

    #endregion

    #region Quota Refresh

    private async Task RefreshAccountAsync(AccountRecord account)
    {
        await _quotaGate.WaitAsync();
        try { await FetchAccountQuotaAsync(account); }
        finally { _quotaGate.Release(); }
    }

    private async Task FetchAccountQuotaAsync(AccountRecord account)
    {
        try
        {
            var usage = await _usage.FetchAsync(await _vault.ReadAuthAsync(account.Id));
            if (!Accounts.Contains(account)) return;
            account.SessionUsed = usage.SessionUsed;
            account.WeeklyUsed = usage.WeeklyUsed;
            account.SessionResetAt = usage.SessionResetAt;
            account.WeeklyResetAt = usage.WeeklyResetAt;
            account.SessionTitle = usage.SessionTitle;
            account.WeeklyTitle = usage.WeeklyTitle;
            account.StatusText = usage.SessionUsed.HasValue || usage.WeeklyUsed.HasValue
                ? $"Updated {DateTimeOffset.Now:HH:mm:ss}"
                : "Quota unavailable: no usage windows returned.";
            if (usage.SessionUsed.HasValue || usage.WeeklyUsed.HasValue)
            {
                try { ApplyResetState(account, await _resetStore.ObserveAsync(account.Id, usage, DateTimeOffset.UtcNow)); }
                catch (Exception exception)
                {
                    AppLog.Error("Save quota reset counts", exception);
                    account.StatusText += " · Could not save reset counts.";
                }
            }
        }
        catch (Exception exception)
        {
            account.SessionUsed = null;
            account.WeeklyUsed = null;
            account.SessionResetAt = null;
            account.WeeklyResetAt = null;
            account.StatusText = exception.Message;
        }
        account.NotifyAll();
    }

    #endregion

    #region Private Methods

    private static void ApplyResetState(AccountRecord account, QuotaResetState? state)
    {
        if (state is null) return;
        account.SessionResetCount = state.SessionCount;
        account.WeeklyResetCount = state.WeeklyCount;
        account.TrackingStartedAt = state.TrackingStartedAt;
    }

    private void NotifySignInState()
    {
        Changed(nameof(CanAddAccount));
        Changed(nameof(CanCancelSignIn));
        Changed(nameof(AddVisibility));
        Changed(nameof(CancelVisibility));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    #endregion
}
