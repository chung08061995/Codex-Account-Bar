using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record UsageWindowResult(
    double UsedPercent,
    string Title,
    string ResetText
);

public sealed record UsageResult(
    UsageWindowResult Primary,
    UsageWindowResult? Secondary,
    int AvailableResetCount,
    bool AutomaticResetApplied
);

public sealed record AccountUsageRefresh(UsageResult Usage, string AuthJson);

public sealed class UsageService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AuthTokenRefreshService _authRefresh = new();

    public async Task<AccountUsageRefresh> FetchRefreshingCredentialAsync(string json)
    {
        try
        {
            return new AccountUsageRefresh(await FetchAsync(json), json);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshedJson = await _authRefresh.RefreshAsync(json);
            return new AccountUsageRefresh(await FetchAsync(refreshedJson), refreshedJson);
        }
    }

    public async Task<UsageResult> FetchAsync(string json)
    {
        var identity = AuthInspector.Inspect(json);
        if (identity.AccessToken is null)
        {
            throw new InvalidDataException("Access token is missing.");
        }

        var result = await FetchUsageAsync(identity.AccessToken, identity.AccountId);
        if (ShouldConsumeReset(result))
        {
            var code = await ConsumeResetAsync(identity.AccessToken, identity.AccountId);
            if (code is "reset" or "already_redeemed")
            {
                var refreshed = await FetchUsageAsync(identity.AccessToken, identity.AccountId);
                return refreshed.Usage with { AutomaticResetApplied = code == "reset" };
            }
            if (code is not "nothing_to_reset" and not "no_credit")
            {
                throw new InvalidDataException($"Codex rejected the available quota reset ({code}).");
            }
        }
        return result.Usage;
    }

    private async Task<UsageEnvelope> FetchUsageAsync(string accessToken, string? accountId)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Get,
            "https://chatgpt.com/backend-api/wham/usage",
            accessToken,
            accountId
        );
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
        var availableResetCount = ResetCount(root, "available_count");
        var applicableResetCount = ResetCount(root, "applicable_available_count");
        var limitReached = rateLimit.TryGetProperty("limit_reached", out var reached) && reached.ValueKind == JsonValueKind.True;
        var allowed = !rateLimit.TryGetProperty("allowed", out var allowedValue) || allowedValue.ValueKind != JsonValueKind.False;

        return new UsageEnvelope(
            new UsageResult(
                ParseWindow(primaryElement) ?? throw new InvalidDataException("Primary quota window is missing."),
                ParseWindow(secondaryElement),
                availableResetCount,
                false
            ),
            limitReached,
            allowed,
            applicableResetCount
        );
    }

    private async Task<string> ConsumeResetAsync(string accessToken, string? accountId)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume",
            accessToken,
            accountId
        );
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { redeem_request_id = Guid.NewGuid().ToString() }),
            Encoding.UTF8,
            "application/json"
        );
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString() ?? "invalid_response"
            : "invalid_response";
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string url,
        string accessToken,
        string? accountId
    )
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (accountId is not null)
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }
        return request;
    }

    private static int ResetCount(JsonElement root, string property)
    {
        return root.TryGetProperty("rate_limit_reset_credits", out var resets)
            && resets.TryGetProperty(property, out var count)
            && count.TryGetInt32(out var value)
                ? value
                : 0;
    }

    private static bool ShouldConsumeReset(UsageEnvelope result)
    {
        return result.ApplicableResetCount > 0
            && (result.LimitReached || !result.Allowed || result.Usage.Primary.UsedPercent >= 100);
    }

    private sealed record UsageEnvelope(
        UsageResult Usage,
        bool LimitReached,
        bool Allowed,
        int ApplicableResetCount
    );

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
