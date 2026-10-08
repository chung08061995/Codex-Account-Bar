using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace CodexAccountBar.Services;

internal static class UsageHttpClient
{
    #region Public Methods

    public static HttpClient Create()
    {
        var handler = new HttpClientHandler();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HTTPS_PROXY")) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALL_PROXY")))
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (settings?.GetValue("ProxyEnable") is int enabled && enabled != 0 && settings.GetValue("ProxyServer") is string configured && ProxyAddress(configured) is { } address)
            {
                var proxy = new WebProxy(address, true);
                var credentials = ReadCredential($"CodexAccountBar:proxy:{address.Host}:{address.Port}") ?? ReadCredential($"AI-Cow:{address.Host}:{address.Port}");
                proxy.Credentials = credentials ?? CredentialCache.DefaultNetworkCredentials;
                if (settings.GetValue("ProxyOverride") is string bypass)
                    proxy.BypassList = bypass.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Trim()).Where(entry => entry != "<local>").Select(entry => "^(?:https?://)?" + System.Text.RegularExpressions.Regex.Escape(entry).Replace("\\*", ".*") + "(?::[0-9]+)?$").ToArray();
                handler.Proxy = proxy;
            }
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    #endregion

    #region Private Methods

    private static Uri? ProxyAddress(string configured)
    {
        var value = configured;
        if (configured.Contains('='))
        {
            var entries = configured.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('=', 2)).Where(entry => entry.Length == 2).ToArray();
            value = entries.FirstOrDefault(entry => entry[0].Equals("https", StringComparison.OrdinalIgnoreCase))?[1]
                ?? entries.FirstOrDefault(entry => entry[0].Equals("http", StringComparison.OrdinalIgnoreCase))?[1] ?? "";
        }
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Contains("://") ? value : "http://" + value, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" ? address : null;
    }

    private static NetworkCredential? ReadCredential(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var password = new SecureString();
            for (var offset = 0; offset < credential.BlobSize; offset += 2) password.AppendChar((char)Marshal.ReadInt16(credential.Blob, offset));
            password.MakeReadOnly();
            var result = new NetworkCredential(credential.UserName, password);
            password.Dispose();
            return result;
        }
        finally { CredFree(pointer); }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr pointer);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr pointer);

    #endregion

    #region Nested Types

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    #endregion
}
