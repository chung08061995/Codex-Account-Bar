using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record UsageWindowResult(
    double UsedPercent,
    string Title,
    string ResetText
);

public sealed record UsageResult(
    UsageWindowResult Primary,
    UsageWindowResult? Secondary
);

public sealed class UsageService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<UsageResult> FetchAsync(string json)
    {
        var identity = AuthInspector.Inspect(json);
        if (identity.AccessToken is null)
        {
            throw new InvalidDataException("Access token is missing.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://chatgpt.com/backend-api/wham/usage"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AccessToken);
        if (identity.AccountId is not null)
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", identity.AccountId);
        }

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var rateLimit = root.TryGetProperty("rate_limit", out var value) ? value : root;
        var primaryElement = rateLimit.TryGetProperty("primary_window", out var primary)
            ? primary
            : default;
        var secondaryElement = rateLimit.TryGetProperty("secondary_window", out var secondary)
            ? secondary
            : default;

        return new UsageResult(
            ParseWindow(primaryElement) ?? throw new InvalidDataException("Primary quota window is missing."),
            ParseWindow(secondaryElement)
        );
    }

    private static UsageWindowResult? ParseWindow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = element.TryGetProperty("used_percent", out var percent)
            && percent.TryGetDouble(out var value)
                ? Math.Clamp(value, 0, 100)
                : 0;
        var windowSeconds = element.TryGetProperty("limit_window_seconds", out var seconds)
            && seconds.TryGetDouble(out var duration)
                ? duration
                : (double?)null;

        return new UsageWindowResult(
            usedPercent,
            WindowTitle(windowSeconds),
            ResetText(element)
        );
    }

    private static string WindowTitle(double? seconds)
    {
        if (seconds is null) return "Quota";
        if (seconds <= 6 * 60 * 60) return "Session";
        if (seconds <= 8 * 24 * 60 * 60) return "Weekly";
        if (seconds <= 35 * 24 * 60 * 60) return "Monthly";
        return "Quota";
    }

    private static string ResetText(JsonElement element)
    {
        if (!element.TryGetProperty("reset_at", out var resetValue)
            || !resetValue.TryGetInt64(out var resetAt))
        {
            return "Reset unknown";
        }

        var reset = DateTimeOffset.FromUnixTimeSeconds(resetAt);
        var remaining = reset - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset due now";
        }

        var countdown = remaining.Days > 0
            ? $"{remaining.Days}d {remaining.Hours}h"
            : remaining.Hours > 0
                ? $"{remaining.Hours}h {remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";
        return $"Resets {reset.ToLocalTime():ddd, dd MMM HH:mm} (in {countdown})";
    }
}
