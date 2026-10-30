using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Client.Core;
using RemoteKSP.Shared.Networking;
using RemoteKSP.Shared.Services;
using RemoteKSP.Shared.Ui;

namespace RemoteKSP.Client.ViewModels;

/// <summary>KSP DLL 的安装状态（只代表文件和注册，不代表远程签名可用）</summary>
public enum KspInstallState { Checking, NotInstalled, NeedsUpdate, Installed, CheckFailed }

/// <summary>首页数据：全部来自本机注册表和 System32 中的 DLL，不访问网络</summary>
public record ClientOverviewSnapshot(KspInstallState Ksp, string? KspError, IReadOnlyList<string> ServerNames,
    int CertificateCount, int CertificateServerCount);

public partial class OverviewViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly Action<Type> _navigate;

    [ObservableProperty] private bool _kspInstalled;
    [ObservableProperty] private bool _kspNeedsInstall;
    [ObservableProperty] private string _kspValue = "检查中…";
    [ObservableProperty] private string _kspDetail = "";
    [ObservableProperty] private StatusKind _kspStatusKind = StatusKind.Busy;
    [ObservableProperty] private string _installKspText = "安装 KSP";

    [ObservableProperty] private int _serverCount;
    [ObservableProperty] private string _serversValue = "检查中…";
    [ObservableProperty] private string _serversDetail = "";
    [ObservableProperty] private string? _serversToolTip;
    [ObservableProperty] private string _serversActionText = "服务端管理";

    [ObservableProperty] private int _certificateCount;
    [ObservableProperty] private string _certificatesValue = "检查中…";
    [ObservableProperty] private string _certificatesDetail = "";
    [ObservableProperty] private string _certificatesActionText = "证书管理";

    [ObservableProperty] private bool _isLoading = true;

    /// <summary>首页「本机网络」卡片；客户端不生成服务端 URL</summary>
    public NetworkAddressesViewModel Network { get; }

    /// <param name="navigate">导航到指定页面（服务端管理、证书管理、连接诊断）</param>
    public OverviewViewModel(NotificationService notificationService, DialogService dialogService, Action<Type> navigate)
    {
        _notificationService = notificationService;
        _navigate = navigate;
        Network = new NetworkAddressesViewModel(dialogService, getPort: null,
            "用于排查连接问题的本机地址",
            "这里列出本机网卡上的地址，用于排查与服务端的连接问题。\n\n" +
            "经过 NAT、代理或 VPN 时，服务端看到的来源地址可能与这里不同。");
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        var network = Network.ActivateAsync();
        await RefreshAsync();
        await network;
        IsLoading = false;
    }

    /// <summary>重新读取 KSP 安装状态、已配对服务端和已绑定证书</summary>
    public async Task RefreshAsync()
    {
        ClientOverviewSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(ReadSnapshot);
        }
        catch (Exception ex)
        {
            snapshot = new ClientOverviewSnapshot(KspInstallState.CheckFailed, ex.Message, [], 0, 0);
        }
        Apply(snapshot);
    }

    private static ClientOverviewSnapshot ReadSnapshot()
    {
        KspInstallState ksp;
        string? kspError = null;
        try
        {
            ksp = !KspInstaller.IsInstalled() ? KspInstallState.NotInstalled
                : KspInstaller.IsUpToDate() ? KspInstallState.Installed
                : KspInstallState.NeedsUpdate;
        }
        catch (Exception ex)
        {
            ksp = KspInstallState.CheckFailed;
            kspError = ex.Message;
        }
        var servers = ClientRegistry.LoadServers();
        var certificates = ClientRegistry.LoadCertificates();
        return new ClientOverviewSnapshot(ksp, kspError, servers.Select(s => s.ServerName).ToList(),
            certificates.Count, certificates.Select(c => c.ServerId).Distinct().Count());
    }

    /// <summary>把读取到的状态映射到三张卡片；只描述真实读取到的内容</summary>
    public void Apply(ClientOverviewSnapshot s)
    {
        KspInstalled = s.Ksp is KspInstallState.Installed or KspInstallState.NeedsUpdate;
        KspNeedsInstall = s.Ksp is KspInstallState.NotInstalled or KspInstallState.NeedsUpdate;
        InstallKspText = s.Ksp == KspInstallState.NeedsUpdate ? "更新 KSP" : "安装 KSP";
        (KspValue, KspDetail, KspStatusKind) = s.Ksp switch
        {
            KspInstallState.Installed => ("已安装", "已注册到 Windows；签名是否可用请运行诊断", StatusKind.Success),
            KspInstallState.NeedsUpdate => ("需要更新", "已安装的 KSP 与当前客户端版本不一致", StatusKind.Caution),
            KspInstallState.NotInstalled => ("未安装", "安装后 SignTool 等应用才能使用远程证书签名", StatusKind.Caution),
            KspInstallState.CheckFailed => ("检查失败", s.KspError ?? "无法读取 KSP 安装状态", StatusKind.Critical),
            _ => ("检查中…", "", StatusKind.Busy)
        };

        ServerCount = s.ServerNames.Count;
        ServersValue = ServerCount == 0 ? "尚未配对服务端" : $"{ServerCount} 台";
        ServersDetail = ServerCount == 0 ? "配对后才能同步服务端授权的证书" : TextSummary.Names(s.ServerNames);
        // 空字符串的 ToolTip 也会弹出空白框；没有服务端时不显示
        ServersToolTip = ServerCount == 0 ? null : string.Join("\n", s.ServerNames);
        ServersActionText = ServerCount == 0 ? "配对服务端" : "管理服务端";

        CertificateCount = s.CertificateCount;
        CertificatesValue = CertificateCount == 0 ? "尚无已绑定证书" : $"{CertificateCount} 张";
        CertificatesDetail = CertificateCount > 0
            ? $"来自 {s.CertificateServerCount} 台服务端，已绑定到 Remote KSP"
            : ServerCount == 0 ? "先配对服务端，再同步证书" : "在证书管理中同步服务端授权的证书";
        CertificatesActionText = CertificateCount == 0 ? "获取证书" : "管理证书";
    }

    // 安装/卸载要复制文件并等待 regsvr32，放到后台线程；AsyncRelayCommand 执行期间按钮自动禁用，避免重复提交
    [RelayCommand]
    private async Task InstallKspAsync()
    {
        try
        {
            await Task.Run(KspInstaller.Install);
        }
        catch (Exception ex)
        {
            await _notificationService.ShowErrorAsync("安装失败", ex.Message);
        }
        // 成功后不另弹通知：卡片状态会更新
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UninstallKspAsync()
    {
        var confirmed = await _notificationService.ConfirmAsync(
            "确认卸载",
            "确定要卸载 Remote KSP 吗？\n\n卸载后将无法使用远程签名功能。",
            "卸载",
            destructive: true);

        if (!confirmed) return;

        try
        {
            await Task.Run(KspInstaller.Uninstall);
        }
        catch (Exception ex)
        {
            await _notificationService.ShowErrorAsync("卸载失败", ex.Message);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        // 诊断需要选择服务端
        if (ServerCount == 0)
        {
            await _notificationService.ShowInfoDialogAsync("需要先配对服务端",
                "请先在「服务端管理」页面配对至少一台服务端，然后再运行诊断。");
            return;
        }

        _navigate(typeof(Views.Pages.DiagnosticsPage));
    }

    [RelayCommand]
    private void OpenServers() => _navigate(typeof(Views.Pages.ServersPage));

    [RelayCommand]
    private void OpenCertificates() => _navigate(typeof(Views.Pages.CertificatesPage));
}
