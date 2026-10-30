using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteKSP.Server.Core;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 证书因 PIN 被拒停用时：活动日志记 Error，并给服务端机器上所有已登录用户弹 Windows 消息。
/// 每次停用只触发一次；之后被拒的签名请求由签名接口各自记日志。
/// </summary>
public class PinAlertService : IHostedService
{
    private readonly KeySourceManager _keySources;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<PinAlertService> _logger;
    private readonly Func<string, string, int> _notify;
    private readonly string? _serverExePath;

    /// <param name="notify">发送 Windows 消息（标题, 正文）→ 送达的会话数；测试时替换，避免真的弹窗</param>
    /// <param name="serverExePath">弹窗里告诉管理员去哪里打开服务端程序；默认是当前进程（服务和 GUI 是同一个 exe）</param>
    public PinAlertService(KeySourceManager keySources, ActivityLogService activityLog,
        ILogger<PinAlertService> logger, Func<string, string, int> notify, string? serverExePath = null)
    {
        _keySources = keySources;
        _activityLog = activityLog;
        _logger = logger;
        _notify = notify;
        _serverExePath = serverExePath ?? Environment.ProcessPath;
    }

    /// <summary>
    /// 弹窗内容。看到弹窗的可能是插 eToken 的管理员，不一定知道 RemoteKSP，
    /// 所以先说明消息来自哪个程序、它在做什么，再给出具体的处理步骤
    /// </summary>
    public static (string Title, string Message) BuildMessage(PinRejectedEvent e, string? serverExePath)
    {
        var f = e.Failure;
        var problem = f.Kind switch
        {
            PinFailureKind.Locked => Strings.Alert_ProblemLocked,
            PinFailureKind.Wrong => Strings.Alert_ProblemWrong,
            _ => Strings.Alert_ProblemUnrecognized
        };
        var open = string.IsNullOrEmpty(serverExePath)
            ? Strings.Alert_Open
            : string.Format(Strings.Alert_OpenPath, serverExePath);
        var steps = string.Format(f.Kind switch
        {
            PinFailureKind.Locked => Strings.Alert_StepsLocked,
            PinFailureKind.Wrong => Strings.Alert_StepsWrong,
            _ => Strings.Alert_StepsUnrecognized
        }, open);

        var title = Strings.Alert_Title;
        var message = string.Format(Strings.Alert_Message, problem, e.CertificateName, e.Thumbprint,
            f.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), steps);
        return (title, message);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _keySources.PinRejected += OnPinRejected;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _keySources.PinRejected -= OnPinRejected;
        return Task.CompletedTask;
    }

    /// <summary>在签名请求线程上调用：日志同步写，弹窗放到后台</summary>
    private void OnPinRejected(PinRejectedEvent e)
    {
        var f = e.Failure;
        _logger.LogError("Certificate {Name} ({Thumbprint}) disabled: eToken rejected PIN ({Kind})",
            e.CertificateName, e.Thumbprint, f.Kind);
        _activityLog.Error(Strings.LogCat_Certificate, string.Format(Strings.Alert_LogDisabled, e.CertificateName, f.Reason),
            string.Format(Strings.Alert_LogDisabledDetail, f.Action));

        var (title, message) = BuildMessage(e, _serverExePath);
        _ = Task.Run(() =>
        {
            try
            {
                var sent = _notify(title, message);
                if (sent == 0)
                    _logger.LogWarning("No interactive session to show the certificate-disabled message");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send certificate-disabled Windows message");
            }
        });
    }
}
