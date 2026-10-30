using System;
using System.Text.Json.Serialization;

namespace RemoteKSP.Server.Core;

/// <summary>
/// 证书注册表条目。每个条目代表服务端管理的一个签名证书。
/// </summary>
public class CertificateEntry
{
    /// <summary>唯一标识符（cert-001, cert-002, ...）</summary>
    public string Id { get; set; } = "";

    /// <summary>用户自定义名称</summary>
    public string Name { get; set; } = "";

    /// <summary>证书指纹（SHA1，小写无分隔符）</summary>
    public string Thumbprint { get; set; } = "";

    /// <summary>密钥源类型：etoken, store, pfx, test</summary>
    public string SourceType { get; set; } = "";

    /// <summary>是否分享给客户端（false = 仅服务端可见）</summary>
    public bool IsShared { get; set; } = true;

    /// <summary>添加时间</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // ===== eToken 类型字段 =====
    /// <summary>CSP 提供程序名称</summary>
    public string? CspProvider { get; set; }

    /// <summary>密钥容器名</summary>
    public string? KeyContainer { get; set; }

    /// <summary>PIN（DPAPI 加密的 base64）</summary>
    public string? PinEncrypted { get; set; }

    /// <summary>token 拒绝 PIN 后的停用原因（Wrong / Locked）；重新验证 PIN 成功后清除</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PinFailureKind? PinFailure { get; set; }

    /// <summary>停用时间（UTC）</summary>
    public DateTime? PinFailedAt { get; set; }

    // ===== 证书存储类型字段 =====
    /// <summary>存储位置：LocalMachine 或 CurrentUser</summary>
    public string? StoreLocation { get; set; }

    // ===== PFX 类型字段 =====
    /// <summary>PFX 文件路径（相对于数据目录或绝对路径）</summary>
    public string? PfxPath { get; set; }

    /// <summary>PFX 密码（DPAPI 加密的 base64）</summary>
    public string? PfxPasswordEncrypted { get; set; }

    // ===== 测试证书类型字段 =====
    /// <summary>测试证书的 DER 编码（base64）。测试证书只下发公钥，签名请求一律拒绝，所以不保存私钥</summary>
    public string? TestCertDer { get; set; }
}
