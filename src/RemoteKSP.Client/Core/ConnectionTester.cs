using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using RemoteKSP.Shared;

namespace RemoteKSP.Client.Core;

public enum TestOutcome { Pass, Warn, Fail, Skip }

public record TestResult(string Name, TestOutcome Outcome, string Message);

/// <summary>
/// 逐层测试客户端到某台服务端、某张证书的链路：连通性 → HMAC 认证 → 远程签名 → 本机 KSP。
/// 测试证书或服务端测试模式下，签名类测试返回 Warn 而不是 Fail。
/// </summary>
public sealed class ConnectionTester : IDisposable
{
    private readonly ServerApi _api;
    private readonly string? _thumbprint;
    private readonly bool _isTestCert;

    public ServerInfo? Info { get; private set; }

    /// <param name="thumbprint">要测试的证书；null 时跳过签名类测试</param>
    public ConnectionTester(ServerEntry server, CertificateBinding? certificate)
    {
        _api = ServerApi.For(server);
        _thumbprint = certificate?.Thumbprint;
        _isTestCert = certificate?.IsTest == true;
    }

    /// <summary>1. 连通性：GET /info（无需认证）</summary>
    public async Task<TestResult> TestConnectivityAsync()
    {
        const string name = "连通性 (HTTPS /info)";
        try
        {
            Info = await _api.GetInfoAsync();
            var multi = Info.SupportsMultiCert ? "支持多证书" : "旧版服务端（单证书）";
            return new TestResult(name, TestOutcome.Pass, $"服务版本 {Info.Version}，{multi}");
        }
        catch (Exception ex)
        {
            return new TestResult(name, TestOutcome.Fail, $"无法连接: {ex.Message}");
        }
    }

    /// <summary>2. HMAC 认证：GET /auth/check</summary>
    public async Task<TestResult> TestAuthAsync()
    {
        const string name = "HMAC 认证 (/auth/check)";
        try
        {
            using var response = await _api.SendAsync(HttpMethod.Get, "/auth/check");
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new TestResult(name, TestOutcome.Pass, "凭据有效"),
                HttpStatusCode.Unauthorized => new TestResult(name, TestOutcome.Fail,
                    "凭据被拒绝：可能已被服务端撤销，或两台机器时钟相差超过 5 分钟"),
                _ => new TestResult(name, TestOutcome.Fail, $"HTTP {(int)response.StatusCode}")
            };
        }
        catch (Exception ex)
        {
            return new TestResult(name, TestOutcome.Fail, ex.Message);
        }
    }

    /// <summary>3. 远程签名：POST /sign/{thumbprint}，用本地证书公钥验证返回的签名</summary>
    public async Task<TestResult> TestSigningAsync()
    {
        const string name = "远程签名 (/sign)";
        if (_thumbprint == null)
            return new TestResult(name, TestOutcome.Skip, "未选择证书");

        try
        {
            var hash = RandomNumberGenerator.GetBytes(32);
            using var response = await _api.SendAsync(HttpMethod.Post, $"/sign/{_thumbprint}",
                Convert.ToBase64String(hash));

            switch (response.StatusCode)
            {
                case HttpStatusCode.ServiceUnavailable:
                    var body = await response.Content.ReadAsStringAsync();
                    // title 是 ASCII "Test Mode"，避免 JSON 转义中文
                    if (body.Contains("\"title\":\"Test Mode\"") || body.Contains("测试模式"))
                        return new TestResult(name, TestOutcome.Warn, "这是测试证书，服务端未执行真实签名（链路正常）");
                    if (response.Headers.TryGetValues(Protocol.HeaderError, out var codes))
                    {
                        var code = codes.FirstOrDefault();
                        if (code == Protocol.ErrorPinLocked)
                            return new TestResult(name, TestOutcome.Fail, "服务端已停用这张证书：eToken 已锁定（PIN 重试次数已用完），请联系服务端管理员");
                        if (code == Protocol.ErrorPinWrong)
                            return new TestResult(name, TestOutcome.Fail, "服务端已停用这张证书：服务端保存的 PIN 错误，请联系服务端管理员");
                    }
                    return new TestResult(name, TestOutcome.Fail, $"服务端签名失败: {body}");
                case HttpStatusCode.Forbidden:
                    return new TestResult(name, TestOutcome.Fail, "服务端未授权本客户端使用这张证书，请点击\"更新证书\"同步");
                case HttpStatusCode.NotFound:
                    return new TestResult(name, TestOutcome.Fail, "服务端已没有这张证书，请点击\"更新证书\"同步");
            }
            if (!response.IsSuccessStatusCode)
                return new TestResult(name, TestOutcome.Fail, $"HTTP {(int)response.StatusCode}");

            var signature = await response.Content.ReadAsByteArrayAsync();
            using var localCert = CertificateBinder.GetCertificate(_thumbprint);
            if (localCert == null)
                return new TestResult(name, TestOutcome.Warn, "签名成功，但本地未安装证书，无法验证");

            using var rsa = localCert.GetRSAPublicKey();
            var valid = rsa?.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return valid == true
                ? new TestResult(name, TestOutcome.Pass, "签名成功并验证通过")
                : new TestResult(name, TestOutcome.Fail, "签名验证失败（证书或密钥不匹配）");
        }
        catch (Exception ex)
        {
            return new TestResult(name, TestOutcome.Fail, ex.Message);
        }
    }

    /// <summary>4. 本地 KSP：通过 Remote KSP 签名（端到端测试 native DLL + 证书绑定 + 注册表映射）</summary>
    public Task<TestResult> TestLocalKspAsync()
    {
        return Task.Run(() =>
        {
            const string name = "本地 KSP (Native)";
            if (_thumbprint == null)
                return new TestResult(name, TestOutcome.Skip, "未选择证书");

            using var localCert = CertificateBinder.GetCertificate(_thumbprint);
            if (localCert == null)
                return new TestResult(name, TestOutcome.Fail, "LocalMachine\\My 中没有这张证书，请点击\"更新证书\"");

            try
            {
                using var rsa = localCert.GetRSAPrivateKey();
                if (rsa == null)
                    return new TestResult(name, TestOutcome.Fail, "证书未绑定私钥");

                var hash = RandomNumberGenerator.GetBytes(32);
                var signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                using var pub = localCert.GetRSAPublicKey();
                var valid = pub?.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return valid == true
                    ? new TestResult(name, TestOutcome.Pass, "通过 Remote KSP 签名成功")
                    : new TestResult(name, TestOutcome.Fail, "签名验证失败");
            }
            catch (CryptographicException ex) when (ex.HResult == unchecked((int)0x80090030)) // NTE_DEVICE_NOT_READY
            {
                if (_isTestCert || Info?.IsTestMode == true)
                    return new TestResult(name, TestOutcome.Warn, "测试证书：Native KSP 返回设备未就绪（链路正常）");
                return new TestResult(name, TestOutcome.Fail, "设备未就绪（服务端 PIN 未验证、eToken 未插入或证书不可用）");
            }
            catch (CryptographicException ex) when (ex.HResult == unchecked((int)0x80090010)) // NTE_PERM
            {
                return new TestResult(name, TestOutcome.Fail, "服务端拒绝：凭据无效或未授权使用这张证书");
            }
            catch (Exception ex)
            {
                return new TestResult(name, TestOutcome.Fail, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public void Dispose() => _api.Dispose();
}
