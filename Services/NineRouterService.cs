using System.Diagnostics; using System.Text.Json; using CodexAccountBar.Models;
namespace CodexAccountBar.Services;
public sealed class NineRouterService
{
    private const int Port=20128;
    public async Task<NineRouterStatus> DetectAsync()
    {
        var svc=await Service();if(svc is{Running:true})return svc;var docker=await Docker();if(docker is{Running:true})return docker;var owner=await Owner();if(owner is not null&&owner.Value.Command.Contains("9router",StringComparison.OrdinalIgnoreCase))return new(true,true,NineRouterKind.NpmCli,$"npm CLI · localhost:{Port}",ProcessId:owner.Value.Pid);
        if(svc is not null)return svc;if(docker is not null)return docker;var cli=await Cli();if(cli is not null)return new(true,false,NineRouterKind.NpmCli,"npm CLI detected",Executable:cli);var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"9router");return Directory.Exists(data)?new(false,false,NineRouterKind.None,"Data found, but no runnable install was detected"):new(false,false,NineRouterKind.None,"Not installed");
    }
    public async Task<NineRouterStatus> SetRunningAsync(bool running)
    {
        var s=await DetectAsync();if(!s.Installed)throw new InvalidOperationException("9Router is not installed.");if(s.Running==running)return s;if(running)await Start(s);else await Stop(s);
        for(var i=0;i<24;i++){await Task.Delay(350);var n=await DetectAsync();if(n.Running==running)return n;}throw new InvalidOperationException($"9Router did not {(running?"start":"stop")} in time.");
    }
    private static async Task Start(NineRouterStatus s)
    {
        if(s.Kind==NineRouterKind.WindowsService&&s.ServiceName is not null){var r=await ProcessRunner.RunAsync("sc.exe",["start",s.ServiceName]);if(r.ExitCode!=0)await Elevated("start",s.ServiceName);}
        else if(s.Kind==NineRouterKind.Docker){var r=await ProcessRunner.RunAsync("docker.exe",["start","9router"],30000);if(r.ExitCode!=0)throw new InvalidOperationException(r.Error);}
        else{var cli=s.Executable??await Cli()??throw new FileNotFoundException("9Router CLI not found.");Process.Start(new ProcessStartInfo(cli){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden,Arguments="--tray --skip-update --port 20128"});}
    }
    private static async Task Stop(NineRouterStatus s)
    {
        if(s.Kind==NineRouterKind.WindowsService&&s.ServiceName is not null){var r=await ProcessRunner.RunAsync("sc.exe",["stop",s.ServiceName]);if(r.ExitCode!=0)await Elevated("stop",s.ServiceName);}
        else if(s.Kind==NineRouterKind.Docker){var r=await ProcessRunner.RunAsync("docker.exe",["stop","9router"],30000);if(r.ExitCode!=0)throw new InvalidOperationException(r.Error);}
        else{var o=await Owner();if(o is null||o.Value.Pid!=s.ProcessId||!o.Value.Command.Contains("9router",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refused to stop: port owner is not verified as 9Router.");var r=await ProcessRunner.RunAsync("taskkill.exe",["/PID",o.Value.Pid.ToString(),"/T","/F"]);if(r.ExitCode!=0)throw new InvalidOperationException(r.Error);}
    }
    private static async Task<NineRouterStatus?> Service()
    {
        const string ps="$s=Get-CimInstance Win32_Service|?{$_.Name -match '9router|nine.?router' -or $_.DisplayName -match '9router|nine.?router'}|select -First 1 Name,State;if($s){$s|ConvertTo-Json -Compress}";try{var r=await ProcessRunner.RunAsync("powershell.exe",["-NoProfile","-NonInteractive","-Command",ps]);if(string.IsNullOrWhiteSpace(r.Output))return null;using var d=JsonDocument.Parse(r.Output);var x=d.RootElement;var name=x.GetProperty("Name").GetString();var state=x.GetProperty("State").GetString();return new(true,state=="Running",NineRouterKind.WindowsService,$"Windows service · {state}",name);}catch{return null;}
    }
    private static async Task<NineRouterStatus?> Docker(){try{var r=await ProcessRunner.RunAsync("docker.exe",["ps","-a","--filter","name=^/9router$","--format","{{.Status}}"],5000);var s=r.Output.Trim();return r.ExitCode==0&&s.Length>0?new(true,s.StartsWith("Up "),NineRouterKind.Docker,"Docker · "+s):null;}catch{return null;}}
    private static async Task<(int Pid,string Command)?> Owner(){var ps=$"$c=Get-NetTCPConnection -LocalPort {Port} -State Listen -ErrorAction SilentlyContinue|select -First 1;if($c){{$id=$c.OwningProcess;$root=$id;$cmd='';for($i=0;$i -lt 6 -and $id -gt 0;$i++){{$p=Get-CimInstance Win32_Process -Filter \"ProcessId=$id\";$cmd+=' '+$p.CommandLine;if($p.CommandLine -match '9router'){{$root=$p.ProcessId;break}};$id=$p.ParentProcessId}};[pscustomobject]@{{Pid=$root;Command=$cmd}}|ConvertTo-Json -Compress}}";try{var r=await ProcessRunner.RunAsync("powershell.exe",["-NoProfile","-NonInteractive","-Command",ps]);if(string.IsNullOrWhiteSpace(r.Output))return null;using var d=JsonDocument.Parse(r.Output);return(d.RootElement.GetProperty("Pid").GetInt32(),d.RootElement.GetProperty("Command").GetString()??"");}catch{return null;}}
    private static async Task<string?> Cli(){try{var r=await ProcessRunner.RunAsync("where.exe",["9router.cmd"],5000);return r.ExitCode==0?r.Output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault():null;}catch{return null;}}
    private static async Task Elevated(string action,string service){var args=$"-NoProfile -Command \"Start-Process sc.exe -Verb RunAs -Wait -ArgumentList '{action}','{service}'\"";var p=Process.Start(new ProcessStartInfo("powershell.exe",args){UseShellExecute=true})??throw new InvalidOperationException("Administrator approval is required.");await p.WaitForExitAsync();}
}
