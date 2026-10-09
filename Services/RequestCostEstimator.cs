using CodexAccountBar.Models;

namespace CodexAccountBar.Services;

public static class RequestCostEstimator
{
    #region Public Methods

    public static decimal? Estimate(RequestLogRecord record)
    {
        if (record.InputTokens is not { } input || record.OutputTokens is not { } output || record.CachedTokens is not { } cached || input < 0 || output < 0 || cached < 0 || cached > input) return null;
        var model = record.Model.StartsWith("cx/", StringComparison.Ordinal) ? record.Model[3..] : record.Model;
        var rates = model switch
        {
            "gpt-6.1-sol" => (Input: 2m, Cached: 0.1m, Output: 10m, LongContext: true),
            "gpt-6-sol" => (Input: 2m, Cached: 0.2m, Output: 10m, LongContext: true),
            "gpt-5.6-sol" => (Input: 4m, Cached: 0.4m, Output: 20m, LongContext: true),
            "gpt-5.5" => (Input: 5m, Cached: 0.5m, Output: 30m, LongContext: true),
            "gpt-6-astra" => (Input: 10m, Cached: 1m, Output: 50m, LongContext: true),
            "gpt-5.3-codex" => (Input: 1.75m, Cached: 0.175m, Output: 14m, LongContext: false),
            _ => (Input: 0m, Cached: 0m, Output: 0m, LongContext: false)
        };
        if (rates.Input == 0m) return null;
        var longContext = rates.LongContext && input > 272000;
        return ((input - cached) * rates.Input * (longContext ? 2m : 1m) + cached * rates.Cached * (longContext ? 2m : 1m) + output * rates.Output * (longContext ? 1.5m : 1m)) / 1000000m;
    }

    #endregion
}
