using System.Windows;
using System.Windows.Controls;
using CodexAccountBar.Models;
using CodexAccountBar.Services;

namespace CodexAccountBar;

public partial class RequestLogWindow : Window
{
    #region Constants

    private const string AllAccounts = "All accounts";

    #endregion

    #region Fields

    private readonly RequestLogService _logs = new();
    private readonly AccountVault _vault = new();
    private readonly CodexService _codex = new();
    private readonly CancellationTokenSource _cancellation = new();
    private IReadOnlyList<RequestLogRecord> _records = [];
    private bool _refreshing;

    #endregion

    #region Framework Lifecycle

    public RequestLogWindow()
    {
        InitializeComponent();
        AccountFilter.ItemsSource = new[] { AllAccounts };
        AccountFilter.SelectedIndex = 0;
        Loaded += Window_Loaded;
        Closed += (_, _) => _cancellation.Cancel();
    }

    #endregion

    #region Event Handlers

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void AccountFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    #endregion

    #region Request Log Loading

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Loading request log...";
        try
        {
            var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var account in await _vault.LoadAsync())
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var identity = AuthInspector.Inspect(await _vault.ReadAuthAsync(account.Id));
                    if (identity.AccountId is { Length: > 0 } id) accounts[id] = account.Email;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Error("Read account identity for request log", exception);
                }
            }
            _records = await _logs.ReadAsync(_codex.CodexHome, accounts, _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested();
            var selected = AccountFilter.SelectedItem as string ?? AllAccounts;
            var choices = new[] { AllAccounts }.Concat(accounts.Values).Concat(_records.Select(record => record.SessionCreator)).Distinct().OrderBy(value => value == AllAccounts ? "" : value).ToList();
            AccountFilter.ItemsSource = choices;
            AccountFilter.SelectedItem = choices.Contains(selected) ? selected : AllAccounts;
            ApplyFilter();
            StatusText.Text = _records.Count == 0 ? "No logged HTTP model requests were found in the last 7 days." : $"Updated {DateTimeOffset.Now:HH:mm:ss}. Showing up to {RequestLogService.MaximumRows} requests retained on this computer.";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _records = [];
            ApplyFilter();
            StatusText.Text = exception.Message;
            AppLog.Error("Load request log", exception);
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        if (LogGrid is null || SummaryText is null) return;
        var account = AccountFilter.SelectedItem as string;
        var rows = account is null || account == AllAccounts ? _records : _records.Where(record => record.SessionCreator == account).ToList();
        LogGrid.ItemsSource = rows;
        SummaryText.Text = $"{rows.Count} requests / {rows.Select(record => record.SessionId).Distinct().Count()} sessions";
    }

    #endregion
}
