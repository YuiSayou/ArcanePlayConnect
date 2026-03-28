using System;
using System.Collections.ObjectModel;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

public class LoggingService
{
    private static readonly Lazy<LoggingService> _instance = new(() => new LoggingService());
    public static LoggingService Instance => _instance.Value;

    public ObservableCollection<LogEntry> Logs { get; } = new();

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    public LoggingService()
    {
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    }

    public void Log(string message, LogLevel level = LogLevel.Info, LogCategory category = LogCategory.System)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message
        };

        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => Logs.Add(entry));
        }
        else
        {
            Logs.Add(entry);
        }
    }

    public void LogInfo(string message, LogCategory category = LogCategory.System) => Log(message, LogLevel.Info, category);
    public void LogWarning(string message, LogCategory category = LogCategory.System) => Log(message, LogLevel.Warning, category);
    public void LogError(string message, LogCategory category = LogCategory.System) => Log(message, LogLevel.Error, category);
    public void LogEvent(string message, LogCategory category = LogCategory.System) => Log(message, LogLevel.Event, category);
}
