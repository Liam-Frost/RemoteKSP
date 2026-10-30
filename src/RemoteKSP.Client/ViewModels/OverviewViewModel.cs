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
    [ObservableProperty] private string _kspValue = Strings.Common_Checking;
    [ObservableProperty] private string _kspDetail = "";
    [ObservableProperty] private StatusKind _kspStatusKind = StatusKind.Busy;
    [ObservableProperty] private string _installKspText = Strings.Ov_InstallKsp;

    [ObservableProperty] private int _serverCount;
    [ObservableProperty] private string _serversValue = Strings.Common_Checking;
    [ObservableProperty] private string _serversDetail = "";
    [ObservableProperty] private string? _serversToolTip;
    [ObservableProperty] private string _serversActionText = Strings.Nav_Servers;

    [ObservableProperty] private int _certificateCount;
    [ObservableProperty] private string _certificatesValue = Strings.Common_Checking;
    [ObservableProperty] private string _certificatesDetail = "";
    [ObservableProperty] private string _certificatesActionText = Strings.Nav_Certificates;

    [ObservableProperty] private bool _isLoading = true;

    /// <summary>首页「本机网络」卡片；客户端不生成服务端 URL</summary>
    public NetworkAddressesViewModel Network { get; }

    /// <param name="navigate">导航到指定页面（服务端管理、证书管理、连接诊断）</param>
    public OverviewViewModel(NotificationService notificationService, DialogService dialogService, Action<Type> navigate)
    {
        _notificationService = notificationService;
        _navigate = navigate;
        Network = new NetworkAddressesViewModel(dialogService, getPort: null,
            Strings.Ov_NetFootnote,
            Strings.Ov_NetDetail);
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
        InstallKspText = s.Ksp == KspInstallState.NeedsUpdate ? Strings.Ov_UpdateKsp : Strings.Ov_InstallKsp;
        (KspValue, KspDetail, KspStatusKind) = s.Ksp switch
        {
            KspInstallState.Installed => (Strings.Ksp_Installed, Strings.Ksp_InstalledDetail, StatusKind.Success),
            KspInstallState.NeedsUpdate => (Strings.Ksp_NeedsUpdate, Strings.Ksp_NeedsUpdateDetail, StatusKind.Caution),
            KspInstallState.NotInstalled => (Strings.Ksp_NotInstalled, Strings.Ksp_NotInstalledDetail, StatusKind.Caution),
            KspInstallState.CheckFailed => (Strings.Ksp_CheckFailed, s.KspError ?? Strings.Ksp_CheckFailedDetail, StatusKind.Critical),
            _ => (Strings.Common_Checking, "", StatusKind.Busy)
        };

        ServerCount = s.ServerNames.Count;
        ServersValue = ServerCount == 0 ? Strings.Ov_NoServers : string.Format(Strings.Ov_ServerCount, ServerCount);
        ServersDetail = ServerCount == 0 ? Strings.Ov_NoServersDetail : TextSummary.Names(s.ServerNames);
        // 空字符串的 ToolTip 也会弹出空白框；没有服务端时不显示
        ServersToolTip = ServerCount == 0 ? null : string.Join("\n", s.ServerNames);
        ServersActionText = ServerCount == 0 ? Strings.Ov_PairServer : Strings.Ov_ManageServers;

        CertificateCount = s.CertificateCount;
        CertificatesValue = CertificateCount == 0 ? Strings.Ov_NoCertificates : string.Format(Strings.Ov_CertificateCount, CertificateCount);
        CertificatesDetail = CertificateCount > 0
            ? string.Format(Strings.Ov_CertsFrom, s.CertificateServerCount)
            : ServerCount == 0 ? Strings.Ov_PairFirst : Strings.Ov_SyncInCertificates;
        CertificatesActionText = CertificateCount == 0 ? Strings.Ov_GetCertificates : Strings.Ov_ManageCertificates;
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
            await _notificationService.ShowErrorAsync(Strings.Common_InstallFailed, ex.Message);
        }
        // 成功后不另弹通知：卡片状态会更新
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UninstallKspAsync()
    {
        var confirmed = await _notificationService.ConfirmAsync(
            Strings.Ov_UninstallTitle,
            Strings.Ov_UninstallMessage,
            Strings.Common_Uninstall,
            destructive: true);

        if (!confirmed) return;

        try
        {
            await Task.Run(KspInstaller.Uninstall);
        }
        catch (Exception ex)
        {
            await _notificationService.ShowErrorAsync(Strings.Common_UninstallFailed, ex.Message);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        // 诊断需要选择服务端
        if (ServerCount == 0)
        {
            await _notificationService.ShowInfoDialogAsync(Strings.Ov_PairFirstTitle,
                Strings.Ov_PairFirstMessage);
            return;
        }

        _navigate(typeof(Views.Pages.DiagnosticsPage));
    }

    [RelayCommand]
    private void OpenServers() => _navigate(typeof(Views.Pages.ServersPage));

    [RelayCommand]
    private void OpenCertificates() => _navigate(typeof(Views.Pages.CertificatesPage));
}
