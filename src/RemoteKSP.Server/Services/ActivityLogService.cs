using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RemoteKSP.Server.Services;

/// <summary>活动日志事件</summary>
public class ActivityLogEvent
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Level { get; init; } = "INFO";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Detail { get; init; }
}

/// <summary>
/// 内存日志收集器，供GUI展示最近的活动日志。
/// PipeServer、KeySourceManager等关键组件在执行操作时记录日志。
/// </summary>
public class ActivityLogService
{
    private readonly ConcurrentQueue<ActivityLogEvent> _logs = new();
    private const int MaxLogs = 500;

    public event Action<ActivityLogEvent>? LogAdded;

    public void Log(string level, string category, string message, string? detail = null)
    {
        var evt = new ActivityLogEvent
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            Detail = detail
        };

        _logs.Enqueue(evt);
        while (_logs.Count > MaxLogs)
            _logs.TryDequeue(out _);

        LogAdded?.Invoke(evt);
    }

    public void Info(string category, string message, string? detail = null) =>
        Log("INFO", category, message, detail);

    public void Warning(string category, string message, string? detail = null) =>
        Log("WARN", category, message, detail);

    public void Error(string category, string message, string? detail = null) =>
        Log("ERROR", category, message, detail);

    public List<ActivityLogEvent> GetRecentLogs(int count = 100) =>
        _logs.TakeLast(Math.Min(count, MaxLogs)).ToList();
}
