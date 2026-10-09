namespace CodexAccountBar.Models;

public sealed record RequestLogRecord(DateTimeOffset Timestamp, string Account, string SessionName, string SessionId, string Model, string Status, string SessionCreator)
{
    #region Properties

    public string LocalTime => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss.fff");

    #endregion
}
