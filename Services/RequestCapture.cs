using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record RequestCapture(DateTimeOffset Timestamp, string AccountId, string ThreadId, string Model, string Status, string RequestId, long? Tokens);

public static class RequestCaptureStore
{
    #region Properties

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountBar", "Requests");

    #endregion

    #region Public Methods

    public static async Task AppendAsync(RequestCapture capture)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{capture.Timestamp.UtcDateTime:yyyyMMdd}.jsonl");
        using var gate = new Mutex(false, "Local\\CodexAccountBar-RequestCapture");
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Request metadata store is busy.");
            File.AppendAllText(path, JsonSerializer.Serialize(capture) + Environment.NewLine);
        }
        finally { if (acquired) gate.ReleaseMutex(); }
        await Task.CompletedTask;
    }

    public static List<RequestCapture> Read()
    {
        var records = new List<RequestCapture>();
        if (!Directory.Exists(Folder)) return records;
        for (var day = 0; day <= 7; day++)
        {
            var path = Path.Combine(Folder, $"{DateTimeOffset.UtcNow.AddDays(-day):yyyyMMdd}.jsonl");
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    if (JsonSerializer.Deserialize<RequestCapture>(line) is { } record && record.Timestamp >= DateTimeOffset.UtcNow.AddDays(-7)) records.Add(record);
                }
                catch (JsonException) { }
            }
        }
        return records.GroupBy(record => record.RequestId).Select(group => group.Last()).OrderByDescending(record => record.Timestamp).Take(RequestLogService.MaximumRows).ToList();
    }

    #endregion
}
