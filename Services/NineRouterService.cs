using System.Diagnostics;
using System.Net.Http;
using System.ServiceProcess;
using CodexAccountBar.Models;

namespace CodexAccountBar.Services;

public sealed class NineRouterService
{
    private const int Port = 20128;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<NineRouterStatus> DetectAsync()
    {
        var service = FindService();
        if (service is { Running: true }) return service;

        var healthy = await IsHealthyAsync();
        var docker = await FindDockerAsync(healthy);
        if (docker is { Running: true }) return docker;

        if (healthy)
            return new(true, true, NineRouterKind.NpmCli, $"9Router API · localhost:{Port}", ProcessId: await FindPortOwnerAsync());

        if (service is not null) return service;
        if (docker is not null) return docker;
        var cli = FindOnPath("9router.cmd") ?? FindOnPath("9router.exe");
        if (cli is not null) return new(true, false, NineRouterKind.NpmCli, "npm CLI detected", Executable: cli);

        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "9router");
        return Directory.Exists(data)
            ? new(false, false, NineRouterKind.None, "9Router data found, but no runnable install was detected")
            : new(false, false, NineRouterKind.None, "Not installed");
    }

    public async Task<NineRouterStatus> SetRunningAsync(bool running)
    {
        var status = await DetectAsync();
        if (!status.Installed) throw new InvalidOperationException("9Router is not installed.");
        if (status.Running == running) return status;
        AppLog.Info($"9Router requested state: {running}; kind: {status.Kind}");
        if (running) await StartAsync(status); else await StopAsync(status);

        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(350);
            var next = await DetectAsync();
            if (next.Running == running) return next;
        }
        throw new InvalidOperationException($"9Router did not {(running ? "start" : "stop")} in time.");
    }

    public async Task EnsureStoppedAsync()
    {
        var status=await DetectAsync();if(status.Installed&&status.Running)await SetRunningAsync(false);
    }

    private async Task StartAsync(NineRouterStatus status)
    {
        if (status.Kind == NineRouterKind.WindowsService && status.ServiceName is not null)
        {
            using var service = new ServiceController(status.ServiceName);
            service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return;
        }
        if (status.Kind == NineRouterKind.Docker)
        {
            var docker = FindOnPath("docker.exe") ?? throw new FileNotFoundException("Docker CLI was not found.");
            var result = await ProcessRunner.RunAsync(docker, ["start", "9router"], 30_000);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            return;
        }
        var cli = status.Executable ?? FindOnPath("9router.cmd") ?? FindOnPath("9router.exe") ?? throw new FileNotFoundException("9Router CLI was not found.");
        Process.Start(new ProcessStartInfo(cli) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, Arguments = "--tray --skip-update --port 20128" });
    }

    private async Task StopAsync(NineRouterStatus status)
    {
        if (status.Kind == NineRouterKind.WindowsService && status.ServiceName is not null)
        {
            using var service = new ServiceController(status.ServiceName);
            service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            return;
        }
        if (status.Kind == NineRouterKind.Docker)
        {
            var docker = FindOnPath("docker.exe") ?? throw new FileNotFoundException("Docker CLI was not found.");
            var result = await ProcessRunner.RunAsync(docker, ["stop", "9router"], 30_000);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            return;
        }
        if (!await IsHealthyAsync()) throw new InvalidOperationException("The service on port 20128 is not verified as 9Router.");
        var pid = status.ProcessId ?? await FindPortOwnerAsync() ?? throw new InvalidOperationException("Could not identify the 9Router process.");
        using var process = Process.GetProcessById(pid);
        var allowed = new[] { "node", "bun", "9router" }.Any(x => process.ProcessName.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (!allowed) throw new InvalidOperationException($"Refused to stop unexpected process: {process.ProcessName}.");
        process.Kill(true); await process.WaitForExitAsync();
    }

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{Port}/api/settings");
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync();
            return body.Contains("provider", StringComparison.OrdinalIgnoreCase) || body.Contains("settings", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static NineRouterStatus? FindService()
    {
        try
        {
            foreach (var service in ServiceController.GetServices())
            {
                using (service)
                {
                    if (!service.ServiceName.Contains("9router", StringComparison.OrdinalIgnoreCase) &&
                        !service.DisplayName.Contains("9router", StringComparison.OrdinalIgnoreCase) &&
                        !service.DisplayName.Contains("nine router", StringComparison.OrdinalIgnoreCase)) continue;
                    var running = service.Status == ServiceControllerStatus.Running;
                    return new(true, running, NineRouterKind.WindowsService, $"Windows service · {service.Status}", service.ServiceName);
                }
            }
        }
        catch (Exception ex) { AppLog.Error("9Router service detection", ex); }
        return null;
    }

    private static async Task<NineRouterStatus?> FindDockerAsync(bool apiRunning)
    {
        var docker = FindOnPath("docker.exe"); if (docker is null) return null;
        try
        {
            var result = await ProcessRunner.RunAsync(docker, ["ps", "-a", "--filter", "name=^/9router$", "--format", "{{.Status}}"], 5_000);
            var value = result.Output.Trim();
            if (result.ExitCode != 0 || value.Length == 0) return null;
            return new(true, apiRunning && value.StartsWith("Up ", StringComparison.OrdinalIgnoreCase), NineRouterKind.Docker, "Docker · " + value);
        }
        catch { return null; }
    }

    private static async Task<int?> FindPortOwnerAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync("netstat.exe", ["-ano", "-p", "TCP"], 5_000);
            foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[1].EndsWith(":" + Port, StringComparison.Ordinal) || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(parts[4], out var pid)) return pid;
            }
        }
        catch { }
        return null;
    }

    private static string? FindOnPath(string file)
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Path.Combine(x.Trim('"'), file)).ToList();
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", file));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", file));
        return candidates.FirstOrDefault(File.Exists);
    }
}
