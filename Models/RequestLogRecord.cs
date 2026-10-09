namespace CodexAccountBar.Models;

public sealed record RequestLogRecord(DateTimeOffset Timestamp, string Account, string SessionName, string SessionId, string Model, string Status, long? Tokens = null, long? InputTokens = null, long? OutputTokens = null, long? CachedTokens = null)
{
    #region Properties

    public string LocalTime => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss.fff");
    public string TokenText => Tokens?.ToString("N0") ?? "—";
    public string InputText => InputTokens?.ToString("N0") ?? "—";
    public string OutputText => OutputTokens?.ToString("N0") ?? "—";
    public string CachedText => CachedTokens?.ToString("N0") ?? "—";
    public string Details { get; init; } = "Authentication and usage captured from the request response.";
    public string ResponseId { get; init; } = "";
    public decimal? EstimatedCost { get; init; }
    public string CostText => EstimatedCost is { } cost ? "$" + cost.ToString("N6", System.Globalization.CultureInfo.InvariantCulture) : InputTokens.HasValue && OutputTokens.HasValue && CachedTokens.HasValue ? "No model rate" : "No usage reported";

    #endregion
}
