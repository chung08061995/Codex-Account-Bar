using System.Diagnostics;
namespace CodexAccountBar.Services;
public sealed record ProcessResult(int ExitCode,string Output,string Error);
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file,IEnumerable<string> args,int timeout=15000,IDictionary<string,string?>? env=null)
    {
        var info=new ProcessStartInfo(file){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WindowStyle=ProcessWindowStyle.Hidden}; foreach(var arg in args)info.ArgumentList.Add(arg); if(env is not null)foreach(var x in env)info.Environment[x.Key]=x.Value;
        using var p=Process.Start(info)??throw new InvalidOperationException($"Could not start {file}.");var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();using var cts=new CancellationTokenSource(timeout);
        try{await p.WaitForExitAsync(cts.Token);}catch(OperationCanceledException){try{p.Kill(true);}catch{}throw new TimeoutException($"{file} timed out.");}return new(p.ExitCode,await stdout,await stderr);
    }
}
