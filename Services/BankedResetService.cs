using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record BankedResetCredit(string Id, DateTimeOffset? ExpiresAt);
public sealed record BankedResetBalance(int AvailableCount, IReadOnlyList<BankedResetCredit> Credits);

public sealed class BankedResetService
{
    #region Fields

    private readonly HttpClient _http;

    #endregion

    #region Initialization

    public BankedResetService(HttpClient? http = null) => _http = http ?? UsageHttpClient.Create();

    #endregion

    #region Public Methods

    public async Task<BankedResetBalance> FetchAsync(string auth)
    {
        using var request = Request(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", auth);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Reset balance unavailable (HTTP {(int)response.StatusCode})." );
        return Parse(await response.Content.ReadAsStringAsync(), DateTimeOffset.UtcNow);
    }

    public async Task ConsumeAsync(string auth, string creditId)
    {
        var balance = await FetchAsync(auth);
        if (!balance.Credits.Any(credit => credit.Id == creditId)) throw new InvalidOperationException("This reset is no longer available. Refresh and try again.");
        using var request = Request(HttpMethod.Post, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume", auth);
        request.Content = JsonContent.Create(new { credit_id = creditId, redeem_request_id = Guid.NewGuid().ToString() });
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Reset request returned HTTP {(int)response.StatusCode}. Refresh before trying again.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var code = document.RootElement.TryGetProperty("code", out var result) ? result.GetString() : null;
        if (code is not ("reset" or "already_redeemed")) throw new InvalidOperationException("Reset was not confirmed by OpenAI. Refresh to check the account.");
    }

    public static BankedResetBalance Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("available_count", out var count) || !count.TryGetInt32(out var available) || available < 0) throw new InvalidDataException("Invalid reset balance response.");
        var credits = new List<BankedResetCredit>();
        if (root.TryGetProperty("credits", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("status", out var status) || status.GetString() != "available" || !item.TryGetProperty("reset_type", out var type) || type.GetString() != "codex_rate_limits" || !item.TryGetProperty("is_supported_by_plan", out var supported) || supported.ValueKind != JsonValueKind.True || !item.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } creditId) continue;
                DateTimeOffset? expires = item.TryGetProperty("expires_at", out var expiry) && expiry.ValueKind == JsonValueKind.String && expiry.TryGetDateTimeOffset(out var date) ? date : null;
                if (expires <= now) continue;
                credits.Add(new(creditId, expires));
            }
        return new(available, credits.OrderBy(credit => credit.ExpiresAt ?? DateTimeOffset.MaxValue).ToArray());
    }

    #endregion

    #region Private Methods

    private static HttpRequestMessage Request(HttpMethod method, string uri, string auth)
    {
        var identity = AuthInspector.Inspect(auth);
        if (identity.AccessToken is null) throw new InvalidDataException("Sign in to this account again.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AccessToken);
        if (identity.AccountId is not null) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", identity.AccountId);
        return request;
    }

    #endregion
}
