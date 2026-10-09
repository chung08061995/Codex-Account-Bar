using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed record RequestUsageEntry(DateTimeOffset Timestamp, string ThreadId, string ResponseId, string Model, long? Input, long? Output, long? Cached, long? Total);

public static class RequestUsageHistory
{
    #region Public Methods

    public static List<RequestUsageEntry> Read(IEnumerable<string?> paths, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, RequestUsageEntry>(StringComparer.Ordinal);
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        foreach (var path in paths.Where(path => !string.IsNullOrEmpty(path)).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var models = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("\"turn_context\"", StringComparison.Ordinal) && !line.Contains("\"token_usage_record\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = Text(root, "type");
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    var turn = Text(payload, "turn_id");
                    if (type == "turn_context")
                    {
                        var model = Text(payload, "model");
                        if (turn.Length > 0 && model.Length > 0) models[turn] = model;
                        continue;
                    }
                    if (type != "token_usage_record" || !DateTimeOffset.TryParse(Text(root, "timestamp"), out var timestamp) || timestamp < since) continue;
                    var response = Text(payload, "response_id");
                    var thread = Text(payload, "thread_id");
                    if (response.Length == 0 || thread.Length == 0 || !payload.TryGetProperty("usage", out var usage)) continue;
                    models.TryGetValue(turn, out var matchedModel);
                    entries[thread + "/" + response] = new(timestamp, thread, response, matchedModel ?? "Model absent from turn metadata", Count(usage, "input_tokens"), Count(usage, "output_tokens"), Count(usage, "cached_input_tokens"), Count(usage, "total_tokens"));
                }
                catch (JsonException) { }
            }
        }
        return entries.Values.OrderByDescending(entry => entry.Timestamp).Take(RequestLogService.MaximumRows).ToList();
    }

    #endregion

    #region Private Methods

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long? Count(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;

    #endregion
}
