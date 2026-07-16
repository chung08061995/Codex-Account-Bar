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
        try
        {
            var psi=new ProcessStartInfo(command.IsCmd?"cmd.exe":command.Path){UseShellExecute=true,WorkingDirectory=home};
            if(command.IsCmd){psi.ArgumentList.Add("/d");psi.ArgumentList.Add("/c");psi.ArgumentList.Add(command.Path);psi.ArgumentList.Add("login");psi.ArgumentList.Add("-c");psi.ArgumentList.Add("cli_auth_credentials_store=\"file\"");}
            else{psi.ArgumentList.Add("login");psi.ArgumentList.Add("-c");psi.ArgumentList.Add("cli_auth_credentials_store=\"file\"");}
            psi.Environment["CODEX_HOME"]=home;
            using var p=Process.Start(psi)??throw new FileNotFoundException("Codex CLI could not be started.");await p.WaitForExitAsync();
            var path=Path.Combine(home,"auth.json");if(p.ExitCode!=0||!File.Exists(path))throw new InvalidOperationException("Codex sign-in failed or was cancelled.");return await File.ReadAllTextAsync(path);
        }
        finally{try{Directory.Delete(home,true);}catch{}}
    }
    private sealed record CodexCommand(string Path,bool IsCmd);
    private static async Task<CodexCommand?> Find(){foreach(var name in new[]{"codex.cmd","codex.bat","codex.exe"}){var r=await ProcessRunner.RunAsync("where.exe",[name],5000);var path=r.ExitCode==0?r.Output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p=>!p.Contains("\\WindowsApps\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)):null;if(path is not null)return new(path,!name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase));}return null;}
    private static async Task Restart()
    {
        var current=Environment.ProcessId;var apps=Process.GetProcesses().Where(p=>p.Id!=current).Select(p=>{try{return (p,p.MainModule?.FileName);}catch{return (p,null);}}).Where(x=>x.Item2 is not null&&x.Item2.Contains("\\WindowsApps\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)).ToList();
        foreach(var (p,_) in apps){try{p.Kill(true);await p.WaitForExitAsync();}catch{}finally{p.Dispose();}}
        try{Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={"shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App"}});}catch{}
    }
}
