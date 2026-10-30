using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RemoteKSP.Client.Core;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Client.ViewModels;

/// <summary>"更新证书"在多个页面共用的交互：指纹冲突确认和结果提示</summary>
public static class SyncUi
{
    /// <summary>同一指纹已被另一台服务端绑定时询问用户；返回 true 表示改绑到新服务端</summary>
    public static Func<CertificateConflict, Task<bool>> ConflictResolver(NotificationService notificationService) =>
        conflict => notificationService.ConfirmAsync(
            "证书已被其他服务端绑定",
            $"证书「{conflict.Name}」已经绑定到服务端「{conflict.ExistingServerName}」。\n\n" +
            $"同一张证书只能由一台服务端签名。要改为使用「{conflict.NewServerName}」吗？选择取消则保留原绑定。",
            "改用新服务端",
            destructive: false,
            new DialogField("证书指纹", conflict.Thumbprint, Monospace: true));

    /// <summary>同步单个服务端的证书并显示结果</summary>
    public static async Task<bool> SyncAndReportAsync(ServerEntry server, NotificationService notificationService)
    {
        try
        {
            var result = await ServerManager.SyncCertificatesAsync(server, ConflictResolver(notificationService));

            // 成功 - 使用模态 Success Dialog
            await notificationService.ShowSyncSuccessAsync(server.ServerName, result.Added, result.Updated, result.Removed);
            return true;
        }
        catch (UnauthorizedException ex)
        {
            // 401 不一定是撤销：时钟偏差、服务端 clients.json 损坏等也会返回 401。由用户决定是否移除
            await HandleUnauthorizedAsync(server, ex, notificationService);
            return false;
        }
        catch (Exception ex)
        {
            var error = ParseSyncError(ex);
            await ShowSyncErrorAsync(server.ServerName, error, notificationService);
            return false;
        }
    }

    /// <summary>
    /// 服务端拒绝了凭据（401）。不自动删除：配对密钥删掉就找不回来，
    /// 而时钟偏差、服务端配置文件损坏后恢复等情况下原凭据其实还能用。让用户确认后再移除
    /// </summary>
    private static async Task HandleUnauthorizedAsync(ServerEntry server, UnauthorizedException error,
        NotificationService notificationService)
    {
        var certCount = ClientRegistry.LoadCertificates().Count(b => b.ServerId == server.Id);
        var remove = await notificationService.ConfirmAsync(
            "服务端拒绝了本客户端的凭据",
            $"服务端「{server.ServerName}」拒绝了请求，证书没有更新。可能的原因：\n\n" +
            "• 服务端管理员撤销了本客户端的配对\n" +
            "• 本机和服务端的时钟相差超过 5 分钟\n" +
            "• 服务端的配对记录丢失或损坏（管理员恢复后原配对可以继续使用）\n\n" +
            $"如果确认已被撤销，可以移除该服务端：本机会删除它的配对凭据，并解绑它提供的 {certCount} 张证书，之后需要重新配对。" +
            "否则请先检查时钟或联系服务端管理员，保留配对。",
            "移除服务端",
            destructive: true,
            new DialogField("服务端返回", error.Message, Monospace: false));
        if (!remove)
            return;

        var (unbound, _) = await ServerManager.RemoveServerAsync(server);
        await notificationService.ShowWarningAsync("已移除服务端",
            $"已移除服务端「{server.ServerName}」，解绑了 {unbound} 张证书。如需继续使用，请重新配对。");
    }

    /// <summary>解析同步异常为结构化错误</summary>
    private static SyncError ParseSyncError(Exception ex)
    {
        var error = new SyncError
        {
            RawError = ex.ToString()
        };

        if (ex is HttpRequestException httpEx)
        {
            error.Type = SyncErrorType.NetworkError;
            error.Summary = "无法连接到服务器";
            error.Detail = "请检查服务器地址是否正确，以及服务器是否正在运行。";
            return error;
        }

        // 尝试解析 HTTP 响应中的 Problem Details
        var message = ex.Message;

        // 检查是否包含 HTTP 状态码
        if (message.Contains("503"))
        {
            error.HttpStatusCode = 503;
            error.Type = SyncErrorType.KeySourceNotReady;
            error.Summary = "签名密钥源尚未就绪";
            error.Detail = "服务端的签名密钥尚未配置或加载。请在服务端配置密钥源（证书存储、eToken 或 PFX 文件）。";
        }
        else if (message.Contains("401"))
        {
            error.HttpStatusCode = 401;
            error.Type = SyncErrorType.AuthenticationError;
            error.Summary = "认证失败";
            error.Detail = "配对信息可能已过期，请尝试重新配对服务端。";
        }
        else if (message.Contains("403"))
        {
            error.HttpStatusCode = 403;
            error.Type = SyncErrorType.AuthenticationError;
            error.Summary = "访问被拒绝";
            error.Detail = "此客户端没有获取证书的权限。";
        }
        else if (message.Contains("500") || message.Contains("502") || message.Contains("504"))
        {
            error.Type = SyncErrorType.ServerError;
            error.Summary = "服务器错误";
            error.Detail = "服务端处理请求时发生错误，请查看服务端日志。";
        }
        else
        {
            error.Type = SyncErrorType.Unknown;
            error.Summary = "同步失败";
            error.Detail = ex.Message;
        }

        return error;
    }

    /// <summary>显示结构化的同步错误</summary>
    private static async Task ShowSyncErrorAsync(string serverName, SyncError error, NotificationService notificationService)
    {
        var fields = new System.Collections.Generic.List<DialogField>();

        // 第一层：服务端和错误摘要
        fields.Add(new DialogField("服务端", serverName, Monospace: false));
        fields.Add(new DialogField("错误", error.Summary, Monospace: false));

        // 第二层：具体说明
        if (!string.IsNullOrWhiteSpace(error.Detail))
        {
            fields.Add(new DialogField("说明", error.Detail, Monospace: false));
        }

        // 第三层：技术详情
        if (error.HttpStatusCode.HasValue)
        {
            fields.Add(new DialogField("HTTP 状态", error.HttpStatusCode.Value.ToString(), Monospace: true));
        }

        if (!string.IsNullOrWhiteSpace(error.RawError))
        {
            fields.Add(new DialogField("技术详情", error.RawError, Monospace: true));
        }

        await notificationService.ShowErrorAsync("证书同步失败", $"无法从服务端「{serverName}」同步证书。", fields.ToArray());
    }
}
