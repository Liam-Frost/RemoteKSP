using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteKSP.Server.Core;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 定期重新加载不可用的证书。典型场景：开机时服务先于 eToken 驱动/中间件启动，首次加载失败。
/// 默认每 30 秒一次、最多 20 次（约 10 分钟）；之后停止，直到 GUI 改动证书或重启服务。
/// </summary>
public class KeySourceRetryService : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    public const int DefaultMaxAttempts = 20;

    private readonly KeySourceManager _keySources;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<KeySourceRetryService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _maxAttempts;

    /// <summary>只有一个构造函数，避免 DI 在多个构造函数间选择；Program.cs 用工厂传入默认值</summary>
    public KeySourceRetryService(KeySourceManager keySources, ActivityLogService activityLog,
        ILogger<KeySourceRetryService> logger, TimeSpan interval, int maxAttempts)
    {
        _keySources = keySources;
        _activityLog = activityLog;
        _logger = logger;
        _interval = interval;
        _maxAttempts = maxAttempts;
        _keySources.ConfigureRetry(maxAttempts);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exhaustedLogged = false;
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RetryResult result;
            try
            {
                result = _keySources.RetryFailed(_maxAttempts);
            }
            catch (Exception ex)
            {
                // 单轮异常不终止重试循环，也不让服务退出
                _logger.LogWarning(ex, "Key source retry failed");
                continue;
            }

            switch (result.Outcome)
            {
                case RetryOutcome.AllRecovered:
                case RetryOutcome.StillFailing when result.Recovered.Count > 0:
                    exhaustedLogged = false;
                    var names = string.Join("、", result.Recovered);
                    _logger.LogInformation("Key sources recovered on attempt {Attempt}: {Names}", result.Attempt, names);
                    _activityLog.Info("证书", $"第 {result.Attempt} 次自动重试后恢复：{names}");
                    break;

                case RetryOutcome.StillFailing:
                    _logger.LogInformation("{Count} key source(s) still unavailable, attempt {Attempt}/{Max}",
                        result.Remaining, result.Attempt, _maxAttempts);
                    break;

                case RetryOutcome.Exhausted when !exhaustedLogged:
                    exhaustedLogged = true;
                    _logger.LogError("{Count} key source(s) still unavailable after {Max} retries; giving up until reload",
                        result.Remaining, _maxAttempts);
                    _activityLog.Error("证书", $"{result.Remaining} 个证书在 {_maxAttempts} 次自动重试后仍不可用，已停止重试",
                        "排除问题（如插入 eToken）后请重启服务，或在 GUI 中重新保存证书配置");
                    break;

                case RetryOutcome.NothingToRetry:
                case RetryOutcome.Superseded:
                    // Reload 之后计数已清零，允许再次记录「已停止重试」
                    exhaustedLogged = false;
                    break;
            }
        }
    }
}
