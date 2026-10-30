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
            Strings.Sync_ConflictTitle,
            string.Format(Strings.Sync_ConflictMessage, conflict.Name, conflict.ExistingServerName, conflict.NewServerName),
            Strings.Sync_SwitchServer,
            destructive: false,
            new DialogField(Strings.Sync_CertThumbprint, conflict.Thumbprint, Monospace: true));

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
            Strings.Sync_RejectedTitle,
            string.Format(Strings.Sync_RejectedMessage, server.ServerName, certCount),
            Strings.Srv_RemoveTitle,
            destructive: true,
            new DialogField(Strings.Sync_ServerReturned, error.Message, Monospace: false));
        if (!remove)
            return;

        var (unbound, _) = await ServerManager.RemoveServerAsync(server);
        await notificationService.ShowWarningAsync(Strings.Sync_ServerRemovedTitle,
            string.Format(Strings.Sync_ServerRemoved, server.ServerName, unbound));
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
            error.Summary = Strings.Sync_CannotConnect;
            error.Detail = Strings.Sync_CannotConnectDetail;
            return error;
        }

        // 尝试解析 HTTP 响应中的 Problem Details
        var message = ex.Message;

        // 检查是否包含 HTTP 状态码
        if (message.Contains("503"))
        {
            error.HttpStatusCode = 503;
            error.Type = SyncErrorType.KeySourceNotReady;
            error.Summary = Strings.Sync_KeySourceNotReady;
            error.Detail = Strings.Sync_KeySourceNotReadyDetail;
        }
        else if (message.Contains("401"))
        {
            error.HttpStatusCode = 401;
            error.Type = SyncErrorType.AuthenticationError;
            error.Summary = Strings.Sync_AuthFailed;
            error.Detail = Strings.Sync_AuthFailedDetail;
        }
        else if (message.Contains("403"))
        {
            error.HttpStatusCode = 403;
            error.Type = SyncErrorType.AuthenticationError;
            error.Summary = Strings.Sync_AccessDenied;
            error.Detail = Strings.Sync_AccessDeniedDetail;
        }
        else if (message.Contains("500") || message.Contains("502") || message.Contains("504"))
        {
            error.Type = SyncErrorType.ServerError;
            error.Summary = Strings.Sync_ServerError;
            error.Detail = Strings.Sync_ServerErrorDetail;
        }
        else
        {
            error.Type = SyncErrorType.Unknown;
            error.Summary = Strings.Sync_Failed;
            error.Detail = ex.Message;
        }

        return error;
    }

    /// <summary>显示结构化的同步错误</summary>
    private static async Task ShowSyncErrorAsync(string serverName, SyncError error, NotificationService notificationService)
    {
        var fields = new System.Collections.Generic.List<DialogField>();

        // 第一层：服务端和错误摘要
        fields.Add(new DialogField(Strings.Common_Server, serverName, Monospace: false));
        fields.Add(new DialogField(Strings.Sync_Error, error.Summary, Monospace: false));

        // 第二层：具体说明
        if (!string.IsNullOrWhiteSpace(error.Detail))
        {
            fields.Add(new DialogField(Strings.Sync_Explanation, error.Detail, Monospace: false));
        }

        // 第三层：技术详情
        if (error.HttpStatusCode.HasValue)
        {
            fields.Add(new DialogField(Strings.Sync_HttpStatus, error.HttpStatusCode.Value.ToString(), Monospace: true));
        }

        if (!string.IsNullOrWhiteSpace(error.RawError))
        {
            fields.Add(new DialogField(Strings.Sync_TechnicalDetail, error.RawError, Monospace: true));
        }

        await notificationService.ShowErrorAsync(Strings.Sync_FailedTitle, string.Format(Strings.Sync_FailedMessage, serverName), fields.ToArray());
    }
}
