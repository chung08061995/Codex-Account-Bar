using System.Text; using System.Text.Json;
namespace CodexAccountBar.Services;
public sealed record AuthIdentity(string Email,string Plan,string? AccountId,string? AccessToken);
public static class AuthInspector
{
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
    private static JsonElement Jwt(string jwt) { var p=jwt.Split('.'); if(p.Length<2) throw new InvalidDataException("Invalid JWT in Codex auth."); var s=p[1].Replace('-','+').Replace('_','/'); s=s.PadRight(s.Length+(4-s.Length%4)%4,'='); using var d=JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s))); return d.RootElement.Clone(); }
    private static string? Str(JsonElement e,string n)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
}
