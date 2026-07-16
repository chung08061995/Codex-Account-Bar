using System.Text;

namespace CodexAccountBar.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexAccountBar", "Logs");
    public static string CurrentFile => Path.Combine(Folder, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string context, Exception exception) => Write("ERROR", $"{context}\n{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
