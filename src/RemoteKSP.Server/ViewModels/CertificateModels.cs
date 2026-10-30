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

    public string SharedText => Status.IsShared ? Strings.Common_Yes : Strings.Common_No;

    public string ExpiryText => Status.NotAfter?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";

    public bool IsExpired => Status.NotAfter is { } t && t < DateTime.UtcNow;

    public bool IsDisabled => Status.DisabledReason != null;

    /// <summary>列表"状态"列：已停用 / 已过期 / 测试 / 待验证 PIN / 就绪 / 不可用</summary>
    public string StateText => Status switch
    {
        _ when IsDisabled => Strings.State_Disabled,
        _ when IsExpired => Strings.State_Expired,
        { SourceType: "test" } => Strings.State_Test,
        { NeedsPin: true } => Strings.State_PinPending,
        { IsReady: true } => Strings.State_Ready,
        _ => Strings.State_Unavailable
    };

    /// <summary>状态徽标配色键：Success / Caution / Critical</summary>
    public string StateKind => Status switch
    {
        _ when IsDisabled || IsExpired => "Critical",
        { SourceType: "test" } or { NeedsPin: true } => "Caution",
        { IsReady: true } => "Success",
        _ => "Critical"
    };

    public string Detail => Status.Detail;

    /// <summary>证书来源描述（详情区域显示）</summary>
    public string SourceText => Status.SourceType switch
    {
        "etoken" => $"{Status.CspProvider} / {Status.KeyContainer}",
        "store" => $"{Status.StoreLocation ?? "LocalMachine"}\\My",
        "pfx" => Status.PfxPath ?? "",
        "test" => Strings.Source_TestVirtual,
        _ => ""
    };

    public static string TypeName(string sourceType) => sourceType switch
    {
        "etoken" => "eToken",
        "store" => Strings.Type_Store,
        "pfx" => Strings.Type_Pfx,
        "test" => Strings.Type_Test,
        _ => sourceType
    };
}

/// <summary>证书存储里的一张候选证书（添加证书对话框的下拉框）</summary>
public record StoreCertOption(string Thumbprint, string Subject, string Issuer, DateTime NotAfter)
{
    public string Display => string.Format(Strings.StoreCert_Display, Subject, Issuer, NotAfter.ToString("yyyy-MM-dd"));
}
