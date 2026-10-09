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
            var choices = new[] { AllAccounts }.Concat(accounts.Values).Concat(_records.Select(record => record.Account)).Distinct().OrderBy(value => value == AllAccounts ? "" : value).ToList();
            AccountFilter.ItemsSource = choices;
            AccountFilter.SelectedItem = choices.Contains(selected) ? selected : AllAccounts;
            ApplyFilter();
            var attributed = _records.Count(record => record.Account != RequestLogService.UnknownAccount && record.Account != "Request omitted account header");
            StatusText.Text = _records.Count == 0 ? "No model response usage or HTTP request records were found in the last 7 days." : $"Updated {DateTimeOffset.Now:HH:mm:ss}. {_records.Count(record => record.InputTokens.HasValue && record.OutputTokens.HasValue)}/{_records.Count} rows have actual token usage; {attributed}/{_records.Count} rows have captured request account identity. Hover a row for its source and response ID.";
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
        var rows = account is null || account == AllAccounts ? _records : _records.Where(record => record.Account == account).ToList();
        var priced = rows.Select(record => record with { EstimatedCost = RequestCostEstimator.Estimate(record) }).ToList();
        LogGrid.ItemsSource = priced;
        SummaryText.Text = $"{rows.Count} requests / {rows.Select(record => record.SessionId).Distinct().Count()} sessions";
        var covered = priced.Count(record => record.EstimatedCost.HasValue);
        var input = TokenSum(rows.Select(record => record.InputTokens));
        var output = TokenSum(rows.Select(record => record.OutputTokens));
        var cached = TokenSum(rows.Select(record => record.CachedTokens));
        var cost = covered == 0 ? "No priced responses" : "$" + priced.Sum(record => record.EstimatedCost ?? 0).ToString("N6", System.Globalization.CultureInfo.InvariantCulture);
        CostSummaryText.Text = $"Input {input} / Output {output} / Cached {cached} tokens | Standard API estimate {cost} ({covered}/{rows.Count} requests priced)";
    }

    private static string TokenSum(IEnumerable<long?> values)
    {
        var known = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return known.Count == 0 ? "No usage reported" : known.Sum().ToString("N0");
    }

    #endregion
}
