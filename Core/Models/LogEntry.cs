using System;

namespace ArcanePlayConnect.Core.Models;

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Event
}

public enum LogCategory
{
    System,
    Webhook,
    Chat,
    Follow,
    Gift,
    Like
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public LogCategory Category { get; set; } = LogCategory.System;
    public string Message { get; set; } = string.Empty;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
}
