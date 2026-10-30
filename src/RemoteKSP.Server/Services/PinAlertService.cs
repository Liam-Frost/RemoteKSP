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
            PinFailureKind.Locked => "eToken 已锁定（PIN 重试次数已用完）",
            PinFailureKind.Wrong => "RemoteKSP 保存的 eToken PIN 错误（可能有人修改过 eToken 的 PIN）",
            _ => "eToken 插着，但签名返回了无法识别的错误（可能是 PIN 错误）"
        };
        var open = string.IsNullOrEmpty(serverExePath)
            ? "以管理员身份打开「RemoteKSP 服务端」程序"
            : $"以管理员身份打开「RemoteKSP 服务端」程序：\n    {serverExePath}";
        var steps = f.Kind switch
        {
            PinFailureKind.Locked =>
                $"1. 用 SafeNet Authentication Client 解锁这个 eToken\n" +
                $"2. {open}\n" +
                $"3. 进入「证书管理」，选中这张证书，点击「验证 PIN」，输入正确的 PIN",
            PinFailureKind.Wrong =>
                $"1. {open}\n" +
                $"2. 进入「证书管理」，选中这张证书，点击「验证 PIN」，输入正确的 PIN",
            _ =>
                $"1. 用 SafeNet Authentication Client 确认这个 eToken 能正常登录\n" +
                $"2. {open}，在「活动日志」里查看原始错误\n" +
                $"3. 进入「证书管理」，选中这张证书，点击「验证 PIN」，输入正确的 PIN"
        };

        var title = "RemoteKSP 远程签名服务：eToken 证书已停用";
        var message =
            "这条消息来自本机的 RemoteKSP 远程签名服务（Windows 服务名：RemoteKSP）。" +
            "它使用插在本机上的 eToken，为其他电脑提供代码签名。\n\n" +
            $"问题：{problem}\n" +
            $"证书：{e.CertificateName}\n" +
            $"指纹：{e.Thumbprint}\n" +
            $"时间：{f.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n\n" +
            "为避免耗尽 eToken 的 PIN 重试次数，RemoteKSP 已停用这张证书并删除保存的 PIN，" +

            "不会再用它访问 eToken。处理之前，其他电脑通过 RemoteKSP 签名都会失败。\n\n" +
            $"处理方法：\n{steps}\n\n" +
            "如果您不负责 RemoteKSP，请把这条消息转给负责代码签名的同事。";
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
        _activityLog.Error("证书", $"证书「{e.CertificateName}」已停用：{f.Reason}",
            $"已删除保存的 PIN，后续签名请求直接拒绝、不再访问 eToken。{f.Action}");

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
