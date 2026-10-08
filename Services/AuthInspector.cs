using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace CodexAccountBar.Services;
public sealed record AuthIdentity(string Email,string Plan,string? AccountId,string? AccessToken);
public static class AuthInspector
{
    #region Public Methods

    public static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("tokens", out _)) return json;
        var access = Str(root, "accessToken") ?? throw new InvalidDataException("JSON does not contain an access token.");
        var refresh = Str(root, "refreshToken");
        var id = Str(root, "idToken") ?? access;
        var provider = root.TryGetProperty("providerSpecificData", out var data) ? data : default;
        var accountId = Str(provider, "chatgptAccountId");
        var normalized = new JsonObject
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = id,
                ["access_token"] = access,
                ["refresh_token"] = refresh,
                ["account_id"] = accountId
            },
            ["last_refresh"] = DateTimeOffset.UtcNow.ToString("O")
        };
        return normalized.ToJsonString();
    }

    #endregion

    #region Inspection

    public static AuthIdentity Inspect(string json)
    {
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        if(!root.TryGetProperty("tokens",out var tokens)) throw new InvalidDataException("This is not a ChatGPT Codex auth file.");
        var access=Str(tokens,"access_token"); var claims=Jwt(Str(tokens,"id_token")??access??throw new InvalidDataException("Codex token is missing."));
        var auth=claims.TryGetProperty("https://api.openai.com/auth",out var a)?a:default;
        var email=Str(claims,"email")??Str(auth,"email")??"Unknown account";
        var account=Str(tokens,"account_id")??Str(claims,"chatgpt_account_id")??Str(auth,"chatgpt_account_id");
        var plan=Str(claims,"chatgpt_plan_type")??Str(auth,"chatgpt_plan_type")??"ChatGPT";
        return new(email,plan.Length>0?char.ToUpperInvariant(plan[0])+plan[1..]:"ChatGPT",account,access);
    }

    #endregion

    #region Private Methods

    private static JsonElement Jwt(string jwt) { var p=jwt.Split('.'); if(p.Length<2) throw new InvalidDataException("Invalid JWT in Codex auth."); var s=p[1].Replace('-','+').Replace('_','/'); s=s.PadRight(s.Length+(4-s.Length%4)%4,'='); using var d=JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s))); return d.RootElement.Clone(); }
    private static string? Str(JsonElement e,string n)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;

    #endregion
}
