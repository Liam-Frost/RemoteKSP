using System;
using RemoteKSP.Server.Core;

namespace RemoteKSP.Server.ViewModels;

/// <summary>证书列表的一行（包装服务返回的 <see cref="CertificateStatus"/>）</summary>
public class CertificateItem
{
    public CertificateStatus Status { get; }

    public CertificateItem(CertificateStatus status) => Status = status;

    public string Id => Status.Id;
    public string Name => Status.Name;
    public string Thumbprint => Status.Thumbprint;
    public bool IsShared => Status.IsShared;
    public bool IsDefault => Status.IsDefault;
    public bool IsEToken => Status.SourceType == "etoken";
    public bool IsReady => Status.IsReady;

    public string TypeText => TypeName(Status.SourceType);

    public string SharedText => Status.IsShared ? "是" : "否";

    public string ExpiryText => Status.NotAfter?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";

    public bool IsExpired => Status.NotAfter is { } t && t < DateTime.UtcNow;

    public bool IsDisabled => Status.DisabledReason != null;

    /// <summary>列表"状态"列：已停用 / 已过期 / 测试 / 待验证 PIN / 就绪 / 不可用</summary>
    public string StateText => Status switch
    {
        _ when IsDisabled => "已停用",
        _ when IsExpired => "已过期",
        { SourceType: "test" } => "测试",
        { NeedsPin: true } => "待验证 PIN",
        { IsReady: true } => "就绪",
        _ => "不可用"
    };

    /// <summary>状态徽标配色键：Success / Caution / Critical</summary>
    public string StateKind => StateText switch
    {
        "就绪" => "Success",
        "测试" or "待验证 PIN" => "Caution",
        _ => "Critical"
    };

    public string Detail => Status.Detail;

    /// <summary>证书来源描述（详情区域显示）</summary>
    public string SourceText => Status.SourceType switch
    {
        "etoken" => $"{Status.CspProvider} / {Status.KeyContainer}",
        "store" => $"{Status.StoreLocation ?? "LocalMachine"}\\My",
        "pfx" => Status.PfxPath ?? "",
        "test" => "自动生成的虚拟证书",
        _ => ""
    };

    public static string TypeName(string sourceType) => sourceType switch
    {
        "etoken" => "eToken",
        "store" => "证书存储",
        "pfx" => "PFX 文件",
        "test" => "测试证书",
        _ => sourceType
    };
}

/// <summary>证书存储里的一张候选证书（添加证书对话框的下拉框）</summary>
public record StoreCertOption(string Thumbprint, string Subject, string Issuer, DateTime NotAfter)
{
    public string Display => $"{Subject}（{Issuer}，{NotAfter:yyyy-MM-dd} 到期）";
}
