using CodexAccountBar.Models;

namespace CodexAccountBar.Services;

public static class RequestCostEstimator
{
    #region Public Methods

    public static decimal? Estimate(RequestLogRecord record)
    {
        if (record.InputTokens is not { } input || record.OutputTokens is not { } output || record.CachedTokens is not { } cached || input < 0 || output < 0 || cached < 0 || cached > input) return null;
        var rates = record.Model switch
        {
            "gpt-6.1-sol" => (Input: 2m, Cached: 0.1m, Output: 10m, LongContext: true),
            "gpt-5.3-codex" => (Input: 1.75m, Cached: 0.175m, Output: 14m, LongContext: false),
            _ => (Input: 0m, Cached: 0m, Output: 0m, LongContext: false)
        };
        if (rates.Input == 0m) return null;
        var longContext = rates.LongContext && input > 272000;
        return ((input - cached) * rates.Input * (longContext ? 2m : 1m) + cached * rates.Cached * (longContext ? 2m : 1m) + output * rates.Output * (longContext ? 1.5m : 1m)) / 1000000m;
    }

    #endregion
}
