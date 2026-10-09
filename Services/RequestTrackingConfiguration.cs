using System.Text.RegularExpressions;

namespace CodexAccountBar.Services;

public static class RequestTrackingConfiguration
{
    #region Properties

    public static string ConfigPath => Path.Combine(new CodexService().CodexHome, "config.toml");
    public static bool Enabled => File.Exists(ConfigPath) && RootSetting(File.ReadAllText(ConfigPath))?.Trim() == $"openai_base_url = \"{RequestRecorder.BaseUrl}\"";

    #endregion

    #region Public Methods

    public static async Task EnableAsync()
    {
        await RequestRecorder.StartAsync();
        var source = File.Exists(ConfigPath) ? await File.ReadAllTextAsync(ConfigPath) : "";
        var existing = RootSetting(source);
        var setting = $"openai_base_url = \"{RequestRecorder.BaseUrl}\"";
        if (existing is not null && existing.Trim() != setting)
            throw new InvalidOperationException("A custom openai_base_url is configured. Request tracking cannot replace another provider endpoint.");
        if (existing is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + $".request-tracking-{DateTimeOffset.Now:yyyyMMddHHmmss}.bak", false);
        await WriteAsync(setting + Environment.NewLine + source);
    }

    public static async Task DisableAsync()
    {
        if (!Enabled) return;
        var source = await File.ReadAllTextAsync(ConfigPath);
        var existing = RootSetting(source)!;
        await WriteAsync(source.Remove(source.IndexOf(existing, StringComparison.Ordinal), existing.Length));
    }

    #endregion

    #region Private Methods

    private static string? RootSetting(string source)
    {
        var root = Regex.Split(source, @"(?m)^\s*\[")[0];
        var match = Regex.Match(root, @"(?m)^\s*openai_base_url\s*=\s*[^\r\n]+");
        return match.Success ? match.Value : null;
    }

    private static async Task WriteAsync(string source)
    {
        var temporary = ConfigPath + ".request-tracking.tmp";
        await File.WriteAllTextAsync(temporary, source);
        File.Move(temporary, ConfigPath, true);
    }

    #endregion
}
