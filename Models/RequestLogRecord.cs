namespace CodexAccountBar.Models;

public sealed record RequestLogRecord(DateTimeOffset Timestamp, string Account, string SessionName, string SessionId, string Model, string Status, long? Tokens = null, long? InputTokens = null, long? OutputTokens = null, long? CachedTokens = null)
{
    #region Properties

    public string LocalTime => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss.fff");
    public string Details { get; init; } = "Authentication and usage captured from the request response.";
    public string ResponseId { get; init; } = "";

    #endregion
}
