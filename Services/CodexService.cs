using System.Diagnostics; using System.Net; using System.Net.Http; using System.Security.Cryptography; using System.Text; using System.Text.Json; using System.Text.Json.Nodes;
namespace CodexAccountBar.Services;
public sealed class CodexService
{
    #region Properties

    public string CodexHome=>Environment.GetEnvironmentVariable("CODEX_HOME") is{Length:>0}v?v:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"); public string AuthPath=>Path.Combine(CodexHome,"auth.json");
    #endregion

    #region Public Methods

    public async Task<string?> ReadActiveAuthAsync()=>File.Exists(AuthPath)?await File.ReadAllTextAsync(AuthPath):null;
    public async Task WriteAndRestartAsync(string json){Directory.CreateDirectory(CodexHome);var normalized=NormalizeAuth(json);var temp=AuthPath+".cab.tmp";await File.WriteAllTextAsync(temp,normalized);File.Move(temp,AuthPath,true);if(!await Restart())throw new InvalidOperationException("The account was saved, but Codex Desktop could not be restarted. Close and reopen Codex manually.");}
    public async Task<string> LoginIsolatedAsync()
    {
        const string clientId="app_EMoamEEZ73f0CkXaXp7hrann";var verifier=Base64Url(RandomNumberGenerator.GetBytes(32));var challenge=Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));var state=Base64Url(RandomNumberGenerator.GetBytes(32));
        HttpListener? listener=null;int port=1455;foreach(var candidate in new[]{1455,1457}){try{listener=new HttpListener();listener.Prefixes.Add($"http://localhost:{candidate}/");listener.Start();port=candidate;break;}catch{listener?.Close();listener=null;}}if(listener is null)throw new InvalidOperationException("Could not start the local login callback. Try closing another Codex login window and retry.");
        using(listener){var redirect=$"http://localhost:{port}/auth/callback";var query=string.Join("&",new Dictionary<string,string>{{"response_type","code"},{"client_id",clientId},{"redirect_uri",redirect},{"scope","openid profile email offline_access api.connectors.read api.connectors.invoke"},{"code_challenge",challenge},{"code_challenge_method","S256"},{"id_token_add_organizations","true"},{"codex_cli_simplified_flow","true"},{"state",state},{"originator","codex_cli_rs"}}.Select(x=>$"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));Process.Start(new ProcessStartInfo($"https://auth.openai.com/oauth/authorize?{query}"){UseShellExecute=true});using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));HttpListenerContext context;try{context=await listener.GetContextAsync().WaitAsync(timeout.Token);}catch(TimeoutException){throw new TimeoutException("Login timed out or was cancelled.");}var code=context.Request.QueryString["code"];var returnedState=context.Request.QueryString["state"];var error=context.Request.QueryString["error"];var response=Encoding.UTF8.GetBytes(error is null?"<html><body>Sign-in complete. You may close this tab.</body></html>":"<html><body>Sign-in was cancelled. You may close this tab.</body></html>");context.Response.ContentType="text/html";context.Response.ContentLength64=response.Length;await context.Response.OutputStream.WriteAsync(response);context.Response.Close();if(error is not null)throw new InvalidOperationException("Codex sign-in was cancelled.");if(code is null||returnedState!=state)throw new InvalidOperationException("Codex sign-in callback was invalid.");using var http=new HttpClient();using var form=new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","authorization_code"},{"code",code},{"redirect_uri",redirect},{"client_id",clientId},{"code_verifier",verifier}});using var tokenResponse=await http.PostAsync("https://auth.openai.com/oauth/token",form);var tokenJson=await tokenResponse.Content.ReadAsStringAsync();if(!tokenResponse.IsSuccessStatusCode)throw new InvalidOperationException("Codex sign-in token exchange failed.");using var tokenDoc=JsonDocument.Parse(tokenJson);var tokens=tokenDoc.RootElement;var idToken=tokens.GetProperty("id_token").GetString();var accessToken=tokens.GetProperty("access_token").GetString();var refreshToken=tokens.GetProperty("refresh_token").GetString();var draft=JsonSerializer.Serialize(new{auth_mode="chatgpt",OPENAI_API_KEY=(string?)null,tokens=new{id_token=idToken,access_token=accessToken,refresh_token=refreshToken,account_id=(string?)null},last_refresh=DateTimeOffset.UtcNow});var accountId=AuthInspector.Inspect(draft).AccountId;return JsonSerializer.Serialize(new{auth_mode="chatgpt",OPENAI_API_KEY=(string?)null,tokens=new{id_token=idToken,access_token=accessToken,refresh_token=refreshToken,account_id=accountId},last_refresh=DateTimeOffset.UtcNow});}
    }
    #endregion

    #region Private Methods

    private static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static string NormalizeAuth(string json){var root=JsonNode.Parse(json)?.AsObject()??throw new InvalidDataException("Invalid Codex auth file.");var accountId=AuthInspector.Inspect(json).AccountId;if(accountId is not null&&root["tokens"] is JsonObject tokens)tokens["account_id"]=accountId;return root.ToJsonString();}
    private static async Task<bool> Restart()
    {
        var current=Environment.ProcessId;var apps=Process.GetProcesses().Where(p=>p.Id!=current).Select(p=>{try{return (p,p.MainModule?.FileName);}catch{return (p,null);}}).Where(x=>x.Item2 is not null&&(x.Item2.Contains("\\WindowsApps\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)||x.Item2.Contains("\\OpenAI\\Codex\\",StringComparison.OrdinalIgnoreCase))).ToList();var desktopPath=apps.Select(x=>x.Item2).FirstOrDefault(x=>string.Equals(Path.GetFileName(x),"ChatGPT.exe",StringComparison.OrdinalIgnoreCase));
        foreach(var (p,_) in apps){try{p.Kill(true);await p.WaitForExitAsync();}catch{}finally{p.Dispose();}}await Task.Delay(700);
        try{Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={"shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App"}});}catch{}await Task.Delay(1800);if(IsDesktopRunning())return true;if(desktopPath is not null)try{Process.Start(new ProcessStartInfo(desktopPath){UseShellExecute=true});}catch{}await Task.Delay(1800);return IsDesktopRunning();
    }
    private static bool IsDesktopRunning()=>Process.GetProcesses().Any(p=>{try{var path=p.MainModule?.FileName;return path is not null&&(path.Contains("\\WindowsApps\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)||path.Contains("\\OpenAI\\Codex\\",StringComparison.OrdinalIgnoreCase));}catch{return false;}finally{p.Dispose();}});
    #endregion
}
