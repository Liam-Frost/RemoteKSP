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
        var name = Strings.Test_Connectivity;
        try
        {
            Info = await _api.GetInfoAsync();
            var multi = Info.SupportsMultiCert ? Strings.Test_MultiCert : Strings.Test_LegacyServer;
            return new TestResult(name, TestOutcome.Pass, string.Format(Strings.Test_ServiceVersion, Info.Version, multi));
        }
        catch (Exception ex)
        {
            return new TestResult(name, TestOutcome.Fail, string.Format(Strings.Test_CannotConnect, ex.Message));
        }
    }

    /// <summary>2. HMAC 认证：GET /auth/check</summary>
    public async Task<TestResult> TestAuthAsync()
    {
        var name = Strings.Test_Hmac;
        try
        {
            using var response = await _api.SendAsync(HttpMethod.Get, "/auth/check");
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new TestResult(name, TestOutcome.Pass, Strings.Test_CredentialsValid),
                HttpStatusCode.Unauthorized => new TestResult(name, TestOutcome.Fail,
                    Strings.Test_CredentialsRejected),
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
        var name = Strings.Test_RemoteSign;
        if (_thumbprint == null)
            return new TestResult(name, TestOutcome.Skip, Strings.Test_NoCertificate);

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
                    // 旧版服务端的测试证书错误只有中文说明
                    if (body.Contains("\"title\":\"Test Mode\"") || body.Contains("测试模式"))
                        return new TestResult(name, TestOutcome.Warn, Strings.Test_TestCertNoSign);
                    if (response.Headers.TryGetValues(Protocol.HeaderError, out var codes))
                    {
                        var code = codes.FirstOrDefault();
                        if (code == Protocol.ErrorPinLocked)
                            return new TestResult(name, TestOutcome.Fail, Strings.Test_DisabledLocked);
                        if (code == Protocol.ErrorPinWrong)
                            return new TestResult(name, TestOutcome.Fail, Strings.Test_DisabledWrongPin);
                    }
                    return new TestResult(name, TestOutcome.Fail, string.Format(Strings.Test_ServerSignFailed, body));
                case HttpStatusCode.Forbidden:
                    return new TestResult(name, TestOutcome.Fail, Strings.Test_NotAllowed);
                case HttpStatusCode.NotFound:
                    return new TestResult(name, TestOutcome.Fail, Strings.Test_CertGone);
            }
            if (!response.IsSuccessStatusCode)
                return new TestResult(name, TestOutcome.Fail, $"HTTP {(int)response.StatusCode}");

            var signature = await response.Content.ReadAsByteArrayAsync();
            using var localCert = CertificateBinder.GetCertificate(_thumbprint);
            if (localCert == null)
                return new TestResult(name, TestOutcome.Warn, Strings.Test_SignedNotInstalled);

            using var rsa = localCert.GetRSAPublicKey();
            var valid = rsa?.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return valid == true
                ? new TestResult(name, TestOutcome.Pass, Strings.Test_SignedVerified)
                : new TestResult(name, TestOutcome.Fail, Strings.Test_VerifyFailedMismatch);
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
            var name = Strings.Test_LocalKsp;
            if (_thumbprint == null)
                return new TestResult(name, TestOutcome.Skip, Strings.Test_NoCertificate);

            using var localCert = CertificateBinder.GetCertificate(_thumbprint);
            if (localCert == null)
                return new TestResult(name, TestOutcome.Fail, Strings.Test_NotInStore);

            try
            {
                using var rsa = localCert.GetRSAPrivateKey();
                if (rsa == null)
                    return new TestResult(name, TestOutcome.Fail, Strings.Test_NoKeyBound);

                var hash = RandomNumberGenerator.GetBytes(32);
                var signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                using var pub = localCert.GetRSAPublicKey();
                var valid = pub?.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return valid == true
                    ? new TestResult(name, TestOutcome.Pass, Strings.Test_KspSigned)
                    : new TestResult(name, TestOutcome.Fail, Strings.Test_VerifyFailed);
            }
            catch (CryptographicException ex) when (ex.HResult == unchecked((int)0x80090030)) // NTE_DEVICE_NOT_READY
            {
                if (_isTestCert || Info?.IsTestMode == true)
                    return new TestResult(name, TestOutcome.Warn, Strings.Test_TestCertNotReady);
                return new TestResult(name, TestOutcome.Fail, Strings.Test_NotReady);
            }
            catch (CryptographicException ex) when (ex.HResult == unchecked((int)0x80090010)) // NTE_PERM
            {
                return new TestResult(name, TestOutcome.Fail, Strings.Test_ServerRefused);
            }
            catch (Exception ex)
            {
                return new TestResult(name, TestOutcome.Fail, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public void Dispose() => _api.Dispose();
}
