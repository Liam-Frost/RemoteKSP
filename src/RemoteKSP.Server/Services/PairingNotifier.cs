using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteKSP.Server.Core;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 收到配对请求时给已登录的管理员发 Windows 通知，这样不开服务端 GUI 也能知道有客户端在等批准。
/// /pair 不需要认证，所以通知要节流：一阵请求合并成一条，两条通知之间至少隔 <see cref="MinInterval"/>，
/// 多个待处理请求时显示总数
/// </summary>
public sealed class PairingNotifier : IHostedService, IDisposable
{
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(30);
    /// <summary>第一条请求到达后稍等一下，把同时到达的请求合并进同一条通知</summary>
    public static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(1);

    private readonly PairingManager _pairingManager;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<PairingToastContent, int> _notify;
    private readonly Func<string?> _tlsThumbprint;
    private readonly object _lock = new();
    private DateTimeOffset _lastShown = DateTimeOffset.MinValue;
    private ITimer? _scheduled;

    public TimeSpan MinInterval { get; }

    /// <param name="notify">显示通知，返回送达的会话数（服务里是 <see cref="UserSessionLauncher.NotifyPairing"/>）</param>
    /// <param name="tlsThumbprint">计算验证码用的服务端 TLS 证书指纹</param>
    public PairingNotifier(PairingManager pairingManager, ILogger logger, TimeProvider time,
        Func<PairingToastContent, int> notify, Func<string?> tlsThumbprint, TimeSpan? minInterval = null)
    {
        _pairingManager = pairingManager;
        _logger = logger;
        _time = time;
        _notify = notify;
        _tlsThumbprint = tlsThumbprint;
        MinInterval = minInterval ?? DefaultMinInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pairingManager.RequestCreated += OnRequestCreated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pairingManager.RequestCreated -= OnRequestCreated;
        lock (_lock)
        {
            _scheduled?.Dispose();
            _scheduled = null;
        }
        return Task.CompletedTask;
    }

    private void OnRequestCreated(PairingRequest request)
    {
        lock (_lock)
        {
            // 已经排了一条：到时会按当时的待处理请求显示，包括这一个
            if (_scheduled != null)
                return;
            var wait = _lastShown + MinInterval - _time.GetUtcNow();
            if (wait < CoalesceDelay)
                wait = CoalesceDelay;
            _scheduled = _time.CreateTimer(_ => Show(), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void Show()
    {
        lock (_lock)
        {
            _scheduled?.Dispose();
            _scheduled = null;
            _lastShown = _time.GetUtcNow();
        }

        // 期间已经批准、拒绝或过期的不再提醒
        var pending = _pairingManager.GetPendingRequests();
        // 同一时刻创建的按到达顺序（稳定排序），取最后一个
        var latest = pending.OrderBy(r => r.CreatedAt).LastOrDefault();
        if (latest == null)
            return;
        try
        {
            var code = RemoteKSP.Shared.Protocol.VerificationCode(_tlsThumbprint() ?? "", latest.ClientNonce, latest.ServerNonce);
            var shown = _notify(new PairingToastContent(pending.Count, latest.MachineName, latest.IpAddress, code, latest.ExpiresAt));
            _logger.LogInformation("Pairing notification for {Count} request(s) shown in {Sessions} session(s)", pending.Count, shown);
        }
        catch (Exception ex)
        {
            // 通知只是提醒，失败不影响配对；请求仍可在 GUI 里处理
            _logger.LogWarning(ex, "Could not show the pairing notification");
        }
    }

    public void Dispose() => _scheduled?.Dispose();
}
