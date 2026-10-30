using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace RemoteKSP.Client.Core;

/// <summary>服务端连接状态</summary>
public enum ServerStatus
{
    Unknown,        // 未检查
    Connected,      // 已连接且授权有效
    Unauthorized,   // 凭据被撤销
    Unreachable,    // 无法连接（离线/网络错误）
    TlsMismatch     // TLS 证书不匹配
}

/// <summary>服务端状态检查</summary>
public static class ServerStatusChecker
{
    /// <summary>检查服务端状态（使用 /auth/check 端点验证授权）</summary>
    public static async Task<ServerStatus> CheckStatusAsync(ServerEntry server)
    {
        try
        {
            using var api = new ServerApi(server.ServerUrl, server.ClientId, server.Secret, server.TlsThumbprint);

            // 使用 /auth/check 端点检查认证状态（需要 HMAC 认证）
            using var response = await api.SendAsync(HttpMethod.Get, "/auth/check");

            if (response.StatusCode == HttpStatusCode.OK)
                return ServerStatus.Connected;
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ServerStatus.Unauthorized;
            else
                return ServerStatus.Unreachable;
        }
        catch (UnauthorizedException)
        {
            return ServerStatus.Unauthorized;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("TLS 证书") && ex.Message.Contains("不一致"))
        {
            return ServerStatus.TlsMismatch;
        }
        catch
        {
            return ServerStatus.Unreachable;
        }
    }

    /// <summary>状态的友好显示文本</summary>
    public static string GetStatusText(ServerStatus status) => status switch
    {
        ServerStatus.Connected => "已连接",
        ServerStatus.Unauthorized => "未授权",
        ServerStatus.Unreachable => "离线",
        ServerStatus.TlsMismatch => "证书错误",
        _ => "未知"
    };

    /// <summary>状态的图标</summary>
    public static string GetStatusIcon(ServerStatus status) => status switch
    {
        ServerStatus.Connected => "Checkmark24",
        ServerStatus.Unauthorized => "DismissCircle24",
        ServerStatus.Unreachable => "PlugDisconnected24",
        ServerStatus.TlsMismatch => "ShieldError24",
        _ => "Question24"
    };

    /// <summary>状态的颜色类型</summary>
    public static string GetStatusKind(ServerStatus status) => status switch
    {
        ServerStatus.Connected => "Success",
        ServerStatus.Unauthorized => "Critical",
        ServerStatus.Unreachable => "Caution",
        ServerStatus.TlsMismatch => "Critical",
        _ => "Neutral"
    };
}
