using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RemoteKSP.Shared;

/// <summary>
/// 服务端、客户端共用的协议定义。原生 KSP（native/ksp/http.cpp）必须保持完全一致。
/// </summary>
public static class Protocol
{
    public const string AuthScheme = "RKSP-HMAC-V2 ";
    public const string HeaderClientId = "X-Client-Id";
    public const string HeaderTimestamp = "X-Timestamp";
    public const string HeaderHashAlgorithm = "X-Hash-Algorithm";
    public const string HeaderNonce = "X-Request-Nonce";
    public const int MaxClockSkewSeconds = 300;

    /// <summary>/info.features 中出现该标记时，服务端支持 GET /certs 多证书接口</summary>
    public const string FeatureMultiCert = "multi-cert";

    /// <summary>签名失败时的机器可读错误码（ASCII，原生 KSP 据此映射错误码）</summary>
    public const string HeaderError = "X-RemoteKSP-Error";
    /// <summary>服务端保存的 PIN 被 eToken 拒绝，证书已停用</summary>
    public const string ErrorPinWrong = "pin-wrong";
    /// <summary>服务端 eToken 已锁定，证书已停用</summary>
    public const string ErrorPinLocked = "pin-locked";

    /// <summary>规范串：METHOD\nPATH\nTIMESTAMP\nHASHALG\nCLIENTID\nBODY\nNONCE</summary>
    public static string Canonical(string method, string path, string timestamp,
        string hashAlgorithm, string clientId, string body, string nonce) =>
        $"{method}\n{path}\n{timestamp}\n{hashAlgorithm}\n{clientId}\n{body}\n{nonce}";

    public static string ComputeSignature(string secret, string canonical)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(mac);
    }

    public static bool SignatureEquals(string expectedBase64, string providedBase64)
    {
        try
        {
            var a = Convert.FromBase64String(expectedBase64);
            var b = Convert.FromBase64String(providedBase64);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 配对校验码：两边各自根据"看到的服务端 TLS 证书指纹 + 双方随机数"计算。
    /// 中间人替换了 TLS 证书，两边的校验码就会不一样。
    /// </summary>
    public static string VerificationCode(string tlsCertThumbprint, string clientNonce, string serverNonce)
    {
        var input = $"{NormalizeThumbprint(tlsCertThumbprint)}:{clientNonce}:{serverNonce}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return value.ToString("D6");
    }

    public static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Replace(":", "").Replace(" ", "").Trim().ToLowerInvariant();

    /// <summary>CNG 算法名 → 服务端支持的哈希算法名；不支持返回 null</summary>
    public static string? NormalizeHashAlgorithm(string? name) => name?.ToUpperInvariant() switch
    {
        "SHA1" => "SHA1",
        "SHA256" => "SHA256",
        "SHA384" => "SHA384",
        "SHA512" => "SHA512",
        _ => null
    };

    public static int HashLength(string hashAlgorithm) => hashAlgorithm switch
    {
        "SHA1" => 20,
        "SHA256" => 32,
        "SHA384" => 48,
        "SHA512" => 64,
        _ => -1
    };
}

/// <summary>GET /certs 的单个条目</summary>
/// <param name="DisabledReason">非 null 表示服务端已停用该证书（PIN 错误或 eToken 已锁），签名会被拒绝</param>
public record RemoteCertificateInfo(string Thumbprint, string Name, string? Subject, DateTime? NotAfter, bool IsTest,
    string? DisabledReason = null);

/// <summary>GET /certs 响应</summary>
public record RemoteCertificateList(List<RemoteCertificateInfo> Certificates);
