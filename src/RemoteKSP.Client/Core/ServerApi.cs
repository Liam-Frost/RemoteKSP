using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using RemoteKSP.Shared;

namespace RemoteKSP.Client.Core;

/// <summary>GET /info 响应</summary>
public record ServerInfo(string? Service, string? Version, string[]? Features, string? Thumbprint, string? Mode)
{
    public bool SupportsMultiCert => Features?.Contains(Protocol.FeatureMultiCert) == true;
    public bool IsTestMode => Mode == "test";
}

/// <summary>
/// 访问单个服务端的 HTTP 客户端。有凭据时请求自动带 HMAC 认证头；
/// 配对时记录过 TLS 指纹的服务端会校验指纹，防止被替换。
/// </summary>
public sealed class ServerApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string? _clientId;
    private readonly string? _secret;
    private readonly string? _pinnedTlsThumbprint;
    private bool _tlsMismatch;

    /// <summary>最近一次连接看到的服务端 TLS 证书指纹</summary>
    public string? LastTlsThumbprint { get; private set; }

    public bool HasCredentials => _clientId != null && _secret != null;

    public ServerApi(string serverUrl, string? clientId = null, string? secret = null, string? pinnedTlsThumbprint = null)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("服务端必须使用 HTTPS。", nameof(serverUrl));
        if (clientId != null && secret != null && string.IsNullOrWhiteSpace(pinnedTlsThumbprint))
            throw new InvalidOperationException("旧配对未记录 TLS 证书指纹，请移除服务端并重新配对。");
        _serverUrl = serverUrl.TrimEnd('/');
        _clientId = clientId;
        _secret = secret;
        _pinnedTlsThumbprint = string.IsNullOrEmpty(pinnedTlsThumbprint) ? null : Protocol.NormalizeThumbprint(pinnedTlsThumbprint);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // 服务端用自签名 TLS 证书：配对前靠验证码防中间人，配对后按记录的指纹校验
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                if (cert == null)
                    return false;
                LastTlsThumbprint = Protocol.NormalizeThumbprint(cert.GetCertHashString());
                _tlsMismatch = _pinnedTlsThumbprint != null && LastTlsThumbprint != _pinnedTlsThumbprint;
                return !_tlsMismatch;
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public static ServerApi For(ServerEntry server) =>
        new(server.ServerUrl, server.ClientId, server.Secret, server.TlsThumbprint);

    public Task<ServerInfo> GetInfoAsync() => Run(async () =>
        await _http.GetFromJsonAsync<ServerInfo>($"{_serverUrl}/info")
            ?? throw new InvalidOperationException("服务端返回了空响应"));

    /// <summary>GET /certs：服务端允许本客户端使用的证书（需要服务端支持 multi-cert）</summary>
    public Task<List<RemoteCertificateInfo>> GetCertificatesAsync() => Run(async () =>
    {
        using var response = await SendAsync(HttpMethod.Get, "/certs");
        await EnsureSuccessAsync(response);
        var list = await response.Content.ReadFromJsonAsync<RemoteCertificateList>();
        return list?.Certificates ?? new List<RemoteCertificateInfo>();
    });

    /// <summary>下载证书公钥（DER）。有凭据时带认证头，服务端据此检查权限</summary>
    public Task<byte[]> DownloadCertificateAsync(string thumbprint) => Run(async () =>
    {
        using var response = await SendAsync(HttpMethod.Get, $"/cert/{Protocol.NormalizeThumbprint(thumbprint)}");
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsByteArrayAsync();
    });

    /// <summary>发送请求；有凭据时按协议签名</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string body = "", string hashAlg = "SHA256") =>
        Run(() =>
        {
            var request = new HttpRequestMessage(method, _serverUrl + path);
            if (method != HttpMethod.Get || body.Length > 0)
                request.Content = new StringContent(body, Encoding.UTF8, "text/plain");

            if (HasCredentials)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var canonical = Protocol.Canonical(method.Method, path, timestamp, hashAlg, _clientId!, body, nonce);
                request.Headers.Add(Protocol.HeaderClientId, _clientId);
                request.Headers.Add(Protocol.HeaderTimestamp, timestamp);
                request.Headers.Add(Protocol.HeaderNonce, nonce);
                request.Headers.Add(Protocol.HeaderHashAlgorithm, hashAlg);
                request.Headers.Add("Authorization", Protocol.AuthScheme + Protocol.ComputeSignature(_secret!, canonical));
            }
            return _http.SendAsync(request);
        });

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var statusCode = response.StatusCode;
        var error = statusCode switch
        {
            HttpStatusCode.Unauthorized => new UnauthorizedException(
                "服务端拒绝了请求：凭据可能已撤销、时钟相差超过 5 分钟、请求重复，或客户端与服务端协议版本不一致"),
            HttpStatusCode.Forbidden => new InvalidOperationException("服务端未授权本客户端使用该证书"),
            HttpStatusCode.NotFound => new InvalidOperationException("服务端没有该证书"),
            _ => new InvalidOperationException($"HTTP {(int)statusCode}: {await response.Content.ReadAsStringAsync()}")
        };

        throw error;
    }

    private async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException ex) when (_tlsMismatch)
        {
            throw new InvalidOperationException(
                $"服务端 TLS 证书（{LastTlsThumbprint}）与配对时记录的不一致，可能遭到中间人攻击。确认服务端重装过后，请移除并重新配对该服务端。", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>服务端返回 401 Unauthorized，凭据已被撤销</summary>
public class UnauthorizedException : InvalidOperationException
{
    public UnauthorizedException(string message) : base(message) { }
}
