using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountBar.Services;

public sealed class AuthTokenRefreshService
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> RefreshAsync(string authJson)
    {
        var root = JsonNode.Parse(authJson)?.AsObject()
            ?? throw new InvalidDataException("The saved Codex auth document is invalid.");
        var tokens = root["tokens"]?.AsObject()
            ?? throw new InvalidDataException("The saved Codex auth document is invalid.");
        var refreshToken = tokens["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidDataException("This account must be switched once to renew its sign-in.");
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken
        });
        using var response = await _http.PostAsync("https://auth.openai.com/oauth/token", form);
        var responseJson = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Codex sign-in refresh failed (HTTP {(int)response.StatusCode}).",
                null,
                response.StatusCode
            );
        }

        using var document = JsonDocument.Parse(responseJson);
        var payload = document.RootElement;
        tokens["access_token"] = RequiredString(payload, "access_token");
        if (OptionalString(payload, "id_token") is { Length: > 0 } idToken)
        {
            tokens["id_token"] = idToken;
        }
        if (OptionalString(payload, "refresh_token") is { Length: > 0 } rotatedRefreshToken)
        {
            tokens["refresh_token"] = rotatedRefreshToken;
        }
        root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
        return root.ToJsonString();
    }

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name)
        ?? throw new InvalidDataException($"Codex sign-in refresh response is missing {name}.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
