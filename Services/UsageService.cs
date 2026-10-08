using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record UsageResult(double? SessionUsed, double? WeeklyUsed, DateTimeOffset? SessionResetAt, DateTimeOffset? WeeklyResetAt, string SessionTitle, string WeeklyTitle);

public sealed class UsageService
{
    #region Fields

    private readonly HttpClient _http = UsageHttpClient.Create();

    #endregion

    #region Public Methods

    public async Task<UsageResult> FetchAsync(string json)
    {
        var identity = AuthInspector.Inspect(json);
        if (identity.AccessToken is null) throw new InvalidDataException("Access token is missing. Sign in again.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AccessToken);
        if (identity.AccountId is not null) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", identity.AccountId);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Quota unavailable: sign in to this account again."
                : $"Quota unavailable (HTTP {(int)response.StatusCode}). Try refreshing again.");
        return ParseResponse(await response.Content.ReadAsStringAsync(), DateTimeOffset.UtcNow);
    }

    public static UsageResult ParseResponse(string json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rate_limit", out var rate) || rate.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Quota unavailable: invalid usage response.");
        var primary = rate.TryGetProperty("primary_window", out var p) ? p : default;
        var secondary = rate.TryGetProperty("secondary_window", out var s) ? s : default;
        var session = primary;
        var weekly = secondary;
        if (Seconds(primary) >= 604800 || (Seconds(secondary) is > 0 and < 604800 && Seconds(primary) > Seconds(secondary)))
            (session, weekly) = (secondary, primary);
        return new(Percent(session), Percent(weekly), ResetAt(session, fetchedAt), ResetAt(weekly, fetchedAt), Title(session, "Session"), Title(weekly, "Weekly"));
    }

    #endregion

    #region Private Methods

    private static double? Percent(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("used_percent", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var value) || !double.IsFinite(value)) return null;
        return Math.Clamp(value, 0, 100);
    }

    private static long? Seconds(JsonElement window) => window.ValueKind == JsonValueKind.Object && window.TryGetProperty("limit_window_seconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt64(out var value) ? value : null;

    private static DateTimeOffset? ResetAt(JsonElement window, DateTimeOffset fetchedAt)
    {
        if (window.ValueKind != JsonValueKind.Object) return null;
        try
        {
            if (window.TryGetProperty("reset_at", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var unix)) return DateTimeOffset.FromUnixTimeSeconds(unix);
            if (window.TryGetProperty("reset_after_seconds", out var after) && after.ValueKind == JsonValueKind.Number && after.TryGetDouble(out var seconds) && double.IsFinite(seconds)) return fetchedAt.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    private static string Title(JsonElement window, string fallback) => Seconds(window) switch
    {
        18000 => "5-hour limit",
        604800 => "Weekly limit",
        >= 86400 and var seconds => $"{seconds / 86400d:0.#}-day limit",
        > 0 and var seconds => $"{seconds / 3600d:0.#}-hour limit",
        _ => fallback
    };

    #endregion
}
