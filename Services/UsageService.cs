using System.Net.Http; using System.Net.Http.Headers; using System.Text.Json;
namespace CodexAccountBar.Services;
public sealed record UsageResult(double SessionUsed,double WeeklyUsed,string SessionReset,string WeeklyReset);
public sealed class UsageService
{
    private readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(15)};
    public async Task<UsageResult> FetchAsync(string json){var i=AuthInspector.Inspect(json);if(i.AccessToken is null)throw new InvalidDataException("Access token is missing.");using var q=new HttpRequestMessage(HttpMethod.Get,"https://chatgpt.com/backend-api/wham/usage");q.Headers.Authorization=new AuthenticationHeaderValue("Bearer",i.AccessToken);if(i.AccountId is not null)q.Headers.TryAddWithoutValidation("ChatGPT-Account-Id",i.AccountId);using var r=await _http.SendAsync(q);r.EnsureSuccessStatusCode();using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());var root=d.RootElement;var rate=root.TryGetProperty("rate_limit",out var x)?x:root;var a=rate.TryGetProperty("primary_window",out var p)?p:default;var b=rate.TryGetProperty("secondary_window",out var s)?s:default;return new(Percent(a),Percent(b),Reset(a),Reset(b));}
    private static double Percent(JsonElement e)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("used_percent",out var p)&&p.TryGetDouble(out var v)?Math.Clamp(v,0,100):0;
    private static string Reset(JsonElement e){if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty("reset_at",out var r)||!r.TryGetInt64(out var n))return"Reset unknown";var t=DateTimeOffset.FromUnixTimeSeconds(n)-DateTimeOffset.UtcNow;return t<=TimeSpan.Zero?"Reset soon":$"Resets in {(t.Days>0?t.Days+"d ":"")}{t.Hours}h";}
}
