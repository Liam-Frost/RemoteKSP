using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Server.Services;

namespace RemoteKSP.Server.ViewModels;

public partial class ActivityLogViewModel : ViewModelBase
{
    private readonly ActivityLogService _logService;
    private bool _isInitializing;

    [ObservableProperty] private ObservableCollection<LogEntry> _logEntries = [];
    [ObservableProperty] private bool _autoScroll = true;
    /// <summary>ALL / INFO / WARN / ERROR (the ComboBoxItem tags, not the displayed text)</summary>
    [ObservableProperty] private string _filterLevel = "ALL";

    public ActivityLogViewModel(ActivityLogService logService)
    {
        _logService = logService;
    }

    public Task InitializeAsync()
    {
        _isInitializing = true;

        // 加载最近的日志
        var recent = _logService.GetRecentLogs(100);
        foreach (var evt in recent)
            LogEntries.Add(new LogEntry(evt));

        _isInitializing = false;

        // 初始化完成后再订阅事件，避免重复添加
        _logService.LogAdded += OnLogAdded;

        _logService.Info(Strings.LogCat_System, Strings.Log_Loaded);
        return Task.CompletedTask;
    }

    private void OnLogAdded(ActivityLogEvent evt)
    {
        if (_isInitializing)
            return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (ShouldShowLog(evt))
                LogEntries.Add(new LogEntry(evt));

            // 保持最多500条
            while (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);
        });
    }

    private bool ShouldShowLog(ActivityLogEvent evt)
    {
        return FilterLevel switch
        {
            "ALL" => true,
            "INFO" => evt.Level == "INFO",
            "WARN" => evt.Level == "WARN",
            "ERROR" => evt.Level == "ERROR",
            _ => true
        };
    }

    [RelayCommand]
    private void Clear()
    {
        LogEntries.Clear();
        // 不再记录"日志已清空"，避免立即添加新条目
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        if (_isInitializing)
            return;

        var recent = _logService.GetRecentLogs(100);
        LogEntries.Clear();
        foreach (var evt in recent.Where(ShouldShowLog))
            LogEntries.Add(new LogEntry(evt));
    }

    partial void OnFilterLevelChanged(string value)
    {
        if (!_isInitializing)
            ApplyFilter();
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; }
    public string Level { get; }
    public string Category { get; }
    public string Message { get; }
    public string? Detail { get; }

    public LogEntry(ActivityLogEvent evt)
    {
        Timestamp = evt.Timestamp;
        Level = evt.Level;
        Category = evt.Category;
        Message = evt.Message;
        Detail = evt.Detail;
    }

    public string TimestampStr => Timestamp.ToString("HH:mm:ss");
    public string LevelDisplay => Level switch
    {
        "INFO" => Strings.Log_LevelInfo,
        "WARN" => Strings.Log_LevelWarn,
        "ERROR" => Strings.Log_LevelError,
        _ => Level
    };
}
