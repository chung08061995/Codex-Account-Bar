using System.Diagnostics; using System.Text.Json;
namespace CodexAccountBar.Services;
public sealed class CodexService
{
    public string CodexHome=>Environment.GetEnvironmentVariable("CODEX_HOME") is{Length:>0}v?v:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"); public string AuthPath=>Path.Combine(CodexHome,"auth.json");
    public async Task<string?> ReadActiveAuthAsync()=>File.Exists(AuthPath)?await File.ReadAllTextAsync(AuthPath):null;
    public async Task WriteAndRestartAsync(string json){Directory.CreateDirectory(CodexHome);using(JsonDocument.Parse(json)){}var temp=AuthPath+".cab.tmp";await File.WriteAllTextAsync(temp,json);File.Move(temp,AuthPath,true);await Restart();}
    public async Task<string> LoginIsolatedAsync()
    {
        var command=await Find()??throw new FileNotFoundException("Codex CLI was not found. Install Codex or add codex.exe/codex.cmd to PATH.");var home=Path.Combine(Path.GetTempPath(),"CodexAccountBar-Login-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
        try{var args=command.IsCmd?new[]{"/d","/c",command.Path,"login","-c","cli_auth_credentials_store=\"file\""}:new[]{"login","-c","cli_auth_credentials_store=\"file\""};ProcessResult r;try{r=await ProcessRunner.RunAsync(command.IsCmd?"cmd.exe":command.Path,args,600000,new Dictionary<string,string?>{{"CODEX_HOME",home}});}catch(InvalidOperationException ex) when(ex.Message.StartsWith("Could not start",StringComparison.OrdinalIgnoreCase)){throw new FileNotFoundException("Codex CLI could not be started.",ex);}var path=Path.Combine(home,"auth.json");if(r.ExitCode!=0||!File.Exists(path))throw new InvalidOperationException("Codex sign-in failed or was cancelled. "+r.Error.Trim());return await File.ReadAllTextAsync(path);}finally{try{Directory.Delete(home,true);}catch{}}
    }
    private sealed record CodexCommand(string Path,bool IsCmd);
    private static async Task<CodexCommand?> Find(){foreach(var name in new[]{"codex.exe","codex.cmd"}){var r=await ProcessRunner.RunAsync("where.exe",[name],5000);var path=r.ExitCode==0?r.Output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault():null;if(path is not null)return new(path,name.EndsWith(".cmd",StringComparison.OrdinalIgnoreCase));}return null;}
    private static async Task Restart(){var apps=Process.GetProcesses().Where(p=>p.ProcessName.Contains("Codex",StringComparison.OrdinalIgnoreCase)&&p.MainWindowHandle!=IntPtr.Zero).ToList();var paths=new List<string>();foreach(var p in apps){try{if(p.MainModule?.FileName is{}path)paths.Add(path);p.Kill(true);await p.WaitForExitAsync();}catch{}finally{p.Dispose();}}foreach(var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch{}}
}
