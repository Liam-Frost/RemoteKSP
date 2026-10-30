using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemoteKSP.Client.Core;

/// <summary>配对客户端</summary>
public class PairingClient
{
    private readonly HttpClient _http;
    public string? LastServerTlsThumbprint { get; private set; }

    public PairingClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (cert == null) return false;
                var thumbprint = cert.GetCertHashString();
                // 首次连接供用户核对验证码；后续轮询固定同一张证书。
                if (LastServerTlsThumbprint != null)
                    return string.Equals(LastServerTlsThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);
                LastServerTlsThumbprint = thumbprint;
                return true;
            }
        };
        _http = new HttpClient(handler);
    }

    /// <summary>发起配对请求</summary>
    public async Task<PairingResponse> StartPairingAsync(string serverUrl, string machineName)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("配对必须使用 HTTPS。", nameof(serverUrl));
        var clientNonce = Guid.NewGuid().ToString("N");
        var request = new { MachineName = machineName, ClientNonce = clientNonce };

        var response = await _http.PostAsJsonAsync($"{serverUrl}/pair", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PairingStartResponse>();
        if (result == null)
            throw new InvalidOperationException("Invalid response");

        return new PairingResponse
        {
            RequestId = result.RequestId,
            ServerNonce = result.ServerNonce,
            ClientNonce = clientNonce
        };
    }

    /// <summary>轮询配对状态</summary>
    public async Task<PairingResult?> CheckPairingStatusAsync(string serverUrl, string requestId)
    {
        var response = await _http.GetAsync($"{serverUrl}/pair/{requestId}");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PairingStatusResponse>();
        if (result == null)
            return null;

        if (result.Status == "approved")
        {
            return new PairingResult
            {
                ClientId = result.ClientId!,
                Secret = result.Secret!
            };
        }

        if (result.Status == "rejected")
            throw new InvalidOperationException("Pairing rejected by server");
        if (result.Status == "expired" || result.Status == "consumed")
            throw new InvalidOperationException("配对请求已过期或凭据已领取，请重新发起配对。");

        return null; // Still pending
    }

    record PairingStartResponse(string RequestId, string ServerNonce, DateTime ExpiresAt);
    record PairingStatusResponse(string Status, string? ClientId, string? Secret);
}

public class PairingResponse
{
    public string RequestId { get; set; } = "";
    public string ServerNonce { get; set; } = "";
    public string ClientNonce { get; set; } = "";
}

public class PairingResult
{
    public string ClientId { get; set; } = "";
    public string Secret { get; set; } = "";
}
