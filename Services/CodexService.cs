using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexAccountBar.Services;

public sealed class CodexService
{
    #region Constants

    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    #endregion

    #region Properties

    public string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } value ? value : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string AuthPath => Path.Combine(CodexHome, "auth.json");

    #endregion

    #region Public Methods

    public async Task<string?> ReadActiveAuthAsync() => File.Exists(AuthPath) ? await File.ReadAllTextAsync(AuthPath) : null;

    public async Task SwitchAccountAsync(string json, CancellationToken cancellationToken = default)
    {
        var normalized = AuthInspector.Normalize(json);
        Directory.CreateDirectory(CodexHome);
        var temporary = AuthPath + ".cab.tmp";
        await File.WriteAllTextAsync(temporary, normalized, cancellationToken);
        File.Move(temporary, AuthPath, true);
        await ReloadVsCodeAsync(cancellationToken);
    }

    public async Task ReloadVsCodeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RestartVsCodeCodexServerAsync(cancellationToken);
        var window = Process.GetProcessesByName("Code").FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);
        if (window is null) throw new InvalidOperationException("Account was switched, but no VS Code window was found.");
        if (!SetForegroundWindow(window.MainWindowHandle)) throw new InvalidOperationException("Account was switched, but VS Code could not be focused for reload.");
        await Task.Delay(150, cancellationToken);
        SendKeys.SendWait("^+p");
        await Task.Delay(150, cancellationToken);
        SendKeys.SendWait("Developer: Reload Window");
        await Task.Delay(150, cancellationToken);
        SendKeys.SendWait("{ENTER}");
    }

    public async Task<string> RefreshAuthAsync(string json, CancellationToken cancellationToken = default)
    {
        var normalized = AuthInspector.Normalize(json);
        var identity = AuthInspector.Inspect(normalized);
        using var document = JsonDocument.Parse(normalized);
        var refreshToken = document.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString();
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new InvalidDataException("Refresh token is missing.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        });
        using var response = await UsageHttpClient.Create().PostAsync("https://auth.openai.com/oauth/token", form, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Refresh token was rejected (HTTP {(int)response.StatusCode}).");
        using var tokenDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var tokens = tokenDocument.RootElement;
        var idToken = tokens.GetProperty("id_token").GetString();
        var accessToken = tokens.GetProperty("access_token").GetString();
        var newRefresh = tokens.TryGetProperty("refresh_token", out var replacement) ? replacement.GetString() : refreshToken;
        var draft = AuthJson(idToken, accessToken, newRefresh, identity.AccountId);
        var accountId = AuthInspector.Inspect(draft).AccountId;
        return AuthJson(idToken, accessToken, newRefresh, accountId);
    }

    public async Task<string> LoginIsolatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = CreateListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var registration = linked.Token.Register(listener.Close);
        var redirect = $"http://localhost:{port}/auth/callback";
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access api.connectors.read api.connectors.invoke",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "codex_cli_rs"
        }.Select(entry => $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value)}"));
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo($"https://auth.openai.com/oauth/authorize?{query}") { UseShellExecute = true });
            var context = await listener.GetContextAsync().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];
            var valid = context.Request.Url?.AbsolutePath == "/auth/callback" && code is not null && context.Request.QueryString["state"] == state && error is null;
            var response = Encoding.UTF8.GetBytes(valid
                ? "<html><body>Sign-in received. You may close this tab.</body></html>"
                : "<html><body>Sign-in failed or was cancelled. Return to Codex Account Bar.</body></html>");
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = response.Length;
            await context.Response.OutputStream.WriteAsync(response, linked.Token);
            context.Response.Close();
            if (!valid) throw new InvalidOperationException(error is null ? "Codex sign-in callback was invalid." : "Codex sign-in was cancelled.");
            using var http = UsageHttpClient.Create();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = redirect,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier
            });
            using var tokenResponse = await http.PostAsync("https://auth.openai.com/oauth/token", form, linked.Token);
            if (!tokenResponse.IsSuccessStatusCode) throw new InvalidOperationException("Codex sign-in token exchange failed.");
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(linked.Token);
            using var tokenDocument = JsonDocument.Parse(tokenJson);
            var tokens = tokenDocument.RootElement;
            var idToken = tokens.GetProperty("id_token").GetString();
            var accessToken = tokens.GetProperty("access_token").GetString();
            var refreshToken = tokens.GetProperty("refresh_token").GetString();
            var draft = AuthJson(idToken, accessToken, refreshToken, null);
            var accountId = AuthInspector.Inspect(draft).AccountId;
            linked.Token.ThrowIfCancellationRequested();
            return AuthJson(idToken, accessToken, refreshToken, accountId);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Codex sign-in timed out. Try adding the account again.");
        }
    }

    #endregion

    #region Private Methods

    private static async Task RestartVsCodeCodexServerAsync(CancellationToken cancellationToken)
    {
        var servers = Process.GetProcessesByName("codex")
            .Where(process =>
            {
                try { return process.MainModule?.FileName.Contains("openai.chatgpt", StringComparison.OrdinalIgnoreCase) == true; }
                catch { return false; }
            })
            .ToArray();
        foreach (var server in servers)
        {
            try
            {
                server.Kill(true);
                await server.WaitForExitAsync(cancellationToken);
                AppLog.Info("Restarted VS Code Codex app-server after account switch.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Error("Restart VS Code Codex app-server", exception);
            }
            finally { server.Dispose(); }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private static HttpListener CreateListener(out int port)
    {
        foreach (var candidate in new[] { 1455, 1457 })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new InvalidOperationException("Could not start the local login callback. Close another Codex login window and retry.");
    }

    private static string AuthJson(string? idToken, string? accessToken, string? refreshToken, string? accountId) => JsonSerializer.Serialize(new
    {
        auth_mode = "chatgpt",
        OPENAI_API_KEY = (string?)null,
        tokens = new { id_token = idToken, access_token = accessToken, refresh_token = refreshToken, account_id = accountId },
        last_refresh = DateTimeOffset.UtcNow
    });

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    #endregion
}
