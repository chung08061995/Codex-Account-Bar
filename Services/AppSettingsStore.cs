using Microsoft.Win32;

namespace CodexAccountBar.Services;

public static class AppSettingsStore
{
    private const string RegistryPath = @"Software\CodexAccountBar";

    public static bool ReadAutoFailoverEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue("AutoFailoverEnabled") is int value && value != 0;
    }

    public static void WriteAutoFailoverEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("AutoFailoverEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
    }
}
