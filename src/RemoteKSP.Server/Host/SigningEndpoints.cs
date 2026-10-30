using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Services;
using RemoteKSP.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteKSP.Server.Host;

public static class SigningEndpoints
{
    private static ClientStore? _clientStore;
    private static PairingManager? _pairingManager;
    private static KeySourceManager? _keySources;
    private static ActivityLogService? _activityLog;
    private static readonly RequestAuth _requestAuth = new();

    public static void MapSigningEndpoints(this WebApplication app,
        ClientStore clientStore,
        PairingManager pairingManager,
        KeySourceManager keySources,
        ActivityLogService activityLog)
    {
        _clientStore = clientStore;
        _pairingManager = pairingManager;
        _keySources = keySources;
        _activityLog = activityLog;

        // 匿名接口：只返回兼容字段（默认证书），不能按客户端过滤，所以不列出全部证书
        app.MapGet("/info", () =>
        {
            var status = _keySources.GetStatus();
            return Results.Ok(new
            {
                service = "RemoteKSP",
                version = typeof(SigningEndpoints).Assembly.GetName().Version!.ToString(3),
                features = new[] { "pairing", "hmac-auth-v2", "test-mode", Protocol.FeatureMultiCert },
                thumbprint = status.Thumbprint,
                mode = status.Mode
            });
        });

        app.MapPost("/pair", (HttpRequest request, PairRequest req) =>
        {
            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var pairRequest = _pairingManager!.CreateRequest(req.MachineName, ip, req.ClientNonce);

            return Results.Ok(new
            {
                requestId = pairRequest.RequestId,
                serverNonce = pairRequest.ServerNonce,
                expiresAt = pairRequest.ExpiresAt
            });
        });

        app.MapGet("/pair/{requestId}", (string requestId) =>
        {
            var claim = _pairingManager!.ClaimApprovedRequest(requestId, credentials =>
            {
                _clientStore!.Add(new PairedClient
                {
                    Id = credentials.ClientId,
                    MachineName = credentials.MachineName,
                    IpAddress = credentials.IpAddress,
                    SecretEncrypted = DpapiHelper.ProtectString(credentials.Secret),
                    PairedAt = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow
                });
            });
            if (claim != null)
            {
                return Results.Ok(new
                {
                    status = "approved",
                    clientId = claim.ClientId,
                    secret = claim.Secret
                });
            }

            var request = _pairingManager.GetRequest(requestId);
            if (request == null)
                return Results.NotFound();
            return Results.Ok(new { status = request.Status.ToString().ToLowerInvariant() });
        });

        // 只验证 HMAC 凭据，不涉及密钥源；客户端"连接测试"用
        app.MapPost("/unpair", async (HttpRequest request) =>
        {
            var client = await AuthenticateRequest(request);
            if (client == null)
                return Results.Unauthorized();

            _clientStore!.Revoke(client.Id);
            _activityLog!.Info("配对", $"客户端「{client.MachineName}」主动解除配对");
            return Results.Ok(new { Success = true });
        });

        app.MapGet("/auth/check", async (HttpRequest request) =>
        {
            if (await AuthenticateRequest(request) == null)
                return Results.Unauthorized();

            var status = _keySources.GetStatus();
            return Results.Ok(new { status = "ok", mode = status.Mode, thumbprint = status.Thumbprint });
        });

        // 当前客户端被允许使用的证书：分享列表 ∩ 客户端的 AllowedCertIds
        app.MapGet("/certs", async (HttpRequest request) =>
        {
            var client = await AuthenticateRequest(request);
            if (client == null)
                return Results.Unauthorized();

            var certs = _keySources.Registry.GetAll()
                .Where(client.CanUse)
                .Select(c =>
                {
                    var (subject, notAfter) = _keySources.GetCertificateInfo(c.Thumbprint);
                    var failure = _keySources.GetPinFailure(c.Thumbprint);
                    return new RemoteCertificateInfo(c.Thumbprint, c.Name, subject, notAfter, c.SourceType == "test",
                        failure == null ? null : $"{failure.Reason}，请联系服务端管理员");
                })
                .ToList();
            return Results.Ok(new RemoteCertificateList(certs));
        });

        // 带认证头：检查该客户端的权限；匿名：只允许下载 /info 里的默认证书（配对阶段旧客户端还没有凭据）
        app.MapGet("/cert/{thumbprint}", async (HttpRequest request, string thumbprint) =>
        {
            var cert = _keySources.Find(thumbprint);
            if (request.Headers.ContainsKey("Authorization"))
            {
                var client = await AuthenticateRequest(request);
                if (client == null)
                    return Results.Unauthorized();
                if (cert == null)
                    return Results.NotFound();
                if (!client.CanUse(cert))
                    return Results.StatusCode(403);
            }
            else if (cert == null || cert.Id != _keySources.DefaultEntry?.Id)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Bytes(_keySources.GetCertificateDer(cert.Thumbprint), "application/x-x509-ca-cert");
            }
            catch (Exception ex)
            {
                return Results.Problem(statusCode: 503, title: "KeySource Not Ready", detail: ex.Message);
            }
        });

        app.MapPost("/sign/{thumbprint}", async (HttpRequest request, string thumbprint) =>
        {
            var client = await AuthenticateRequest(request);
            if (client == null)
                return Results.Unauthorized();

            var cert = _keySources.Find(thumbprint);
            if (cert == null)
                return Results.Problem(statusCode: 404, title: "Unknown Key",
                    detail: "服务端没有这张证书，请在客户端点击\"更新证书\"");
            if (!client.CanUse(cert))
                return Results.Problem(statusCode: 403, title: "Forbidden",
                    detail: "服务端未授权该客户端使用这张证书");

            try
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
                var hash = Convert.FromBase64String(await reader.ReadToEndAsync());

                var hashAlgorithm = Protocol.NormalizeHashAlgorithm(request.Headers[Protocol.HeaderHashAlgorithm].ToString())
                    ?? "SHA256";
                if (hash.Length != Protocol.HashLength(hashAlgorithm))
                    return Results.BadRequest($"哈希长度与算法 {hashAlgorithm} 不匹配");

                var signature = _keySources.SignHash(cert.Thumbprint, hash, hashAlgorithm);
                _activityLog?.Info("签名", $"{client.MachineName} 使用证书签名", $"{cert.Name} ({hashAlgorithm})");
                return Results.Bytes(signature, "application/octet-stream");
            }
            catch (TestModeException ex)
            {
                // title 是客户端识别测试模式的依据（detail 中文会被 JSON 转义）
                _activityLog?.Warning("签名", $"{client.MachineName} 尝试用测试证书签名", cert.Name);
                return Results.Problem(statusCode: 503, title: "Test Mode", detail: ex.Message);
            }
            catch (PinRejectedException ex)
            {
                // 证书已停用：本次请求之外不会再访问 token。错误码放在 ASCII 头和 title 里，原生 KSP/客户端据此识别
                // 无法识别的错误不带 X-RemoteKSP-Error：原生 KSP 返回"设备未就绪"，不冒充 PIN 错误
                var kind = ex.Failure.Kind;
                _activityLog?.Error("签名", $"{client.MachineName} 签名被拒绝：证书已停用",
                    $"{cert.Name}: {ex.Failure.Reason}" + (ex.InnerException is { } inner ? $"（原始错误 0x{inner.HResult:X8}: {inner.Message}）" : ""));
                if (kind != PinFailureKind.Unrecognized)
                    request.HttpContext.Response.Headers[Protocol.HeaderError] =
                        kind == PinFailureKind.Locked ? Protocol.ErrorPinLocked : Protocol.ErrorPinWrong;
                return Results.Problem(statusCode: 503,
                    title: kind switch { PinFailureKind.Locked => "PIN Locked", PinFailureKind.Wrong => "PIN Wrong", _ => "Disabled" },
                    detail: $"服务端证书「{cert.Name}」已停用：{ex.Failure.Reason}。请联系服务端管理员处理。");
            }
            catch (Exception ex)
            {
                _activityLog?.Error("签名", $"{client.MachineName} 签名失败", $"{cert.Name}: {ex.Message}");
                return Results.Problem(statusCode: 503, title: "Sign Failed", detail: ex.Message);
            }
        });
    }

    /// <summary>校验 HMAC 认证头；成功返回对应客户端，失败返回 null</summary>
    private static async Task<PairedClient?> AuthenticateRequest(HttpRequest request)
    {
        var clientId = request.Headers[Protocol.HeaderClientId].ToString();
        var timestamp = request.Headers[Protocol.HeaderTimestamp].ToString();
        var nonce = request.Headers[Protocol.HeaderNonce].ToString();
        var hashAlgorithm = request.Headers[Protocol.HeaderHashAlgorithm].ToString();
        var authHeader = request.Headers["Authorization"].ToString();

        if (string.IsNullOrEmpty(clientId) || !authHeader.StartsWith(Protocol.AuthScheme))
            return null;

        var client = _clientStore!.GetById(clientId);
        if (client == null)
            return null;

        var secret = DpapiHelper.UnprotectString(client.SecretEncrypted);

        // body 是客户端发来的 base64 文本，原样参与 HMAC
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        var isValid = _requestAuth.VerifySignature(request.Method, request.Path, timestamp, hashAlgorithm,
            clientId, body, nonce, authHeader.Substring(Protocol.AuthScheme.Length), secret);
        if (!isValid)
            return null;

        _clientStore.UpdateLastSeen(clientId);
        return client;
    }

    public record PairRequest(string MachineName, string ClientNonce);
}
