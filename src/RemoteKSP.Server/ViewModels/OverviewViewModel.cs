using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Services;
using RemoteKSP.Shared.Networking;
using RemoteKSP.Shared.Services;
using RemoteKSP.Shared.Ui;

namespace RemoteKSP.Server.ViewModels;

/// <summary>
/// 签名密钥源卡片的状态。Windows 服务运行中 ≠ 管理通道可用 ≠ 密钥源就绪，三层分开表达
/// </summary>
public enum KeySourceState
{
    Loading,
    /// <summary>Windows 服务未安装或未运行，密钥源由服务加载，无从查询</summary>
    ServiceNotRunning,
    /// <summary>服务正在启动或停止</summary>
    ServicePending,
    /// <summary>服务在运行，但管理通道（命名管道）连不上</summary>
    ChannelUnavailable,
    /// <summary>GUI 没有权限访问管理通道</summary>
    ChannelAccessDenied,
    /// <summary>通道可用，但服务处理查询时报错</summary>
    QueryFailed,
    /// <summary>没有分享的证书，客户端拿不到证书</summary>
    NotConfigured,
    /// <summary>默认证书是测试证书，或加载失败（服务回报为测试模式）</summary>
    NotReady,
    Ready
}

/// <summary>一次状态刷新的结果；全部来自 SCM 和管理通道的真实回复</summary>
public record ServerOverviewSnapshot(ServiceState Service, KeySourceState KeySource, KeySourceStatus? KeySourceStatus,
    string? KeySourceError, int? ClientCount, string? ClientError);

public partial class OverviewViewModel : ViewModelBase
{
    private readonly ServiceManager _serviceManager;
    private readonly PipeClient _pipeClient;
    private readonly DialogService _dialogService;
    private readonly Action<Type> _navigate;
    private CancellationTokenSource? _refreshCts;

    // Windows 服务
    [ObservableProperty] private ServiceStatusKind _serviceKind = ServiceStatusKind.Unknown;
    [ObservableProperty] private string _serviceValue = Strings.Common_Checking;
    [ObservableProperty] private string _serviceDetail = "";
    [ObservableProperty] private StatusKind _serviceIndicator = StatusKind.Busy;
    [ObservableProperty] private bool _serviceInstalled;
    [ObservableProperty] private bool _serviceRunning;
    [ObservableProperty] private bool _canInstallService;
    [ObservableProperty] private bool _canStartService;
    [ObservableProperty] private bool _canStopService;

    /// <summary>启动/停止/安装/卸载进行中：所有服务操作按钮禁用，避免重复提交</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallServiceCommand), nameof(StartServiceCommand),
        nameof(StopServiceCommand), nameof(UninstallServiceCommand), nameof(RefreshStatusCommand))]
    private bool _isServiceBusy;

    // 签名密钥源
    [ObservableProperty] private KeySourceState _keySourceState = KeySourceState.Loading;
    [ObservableProperty] private string _keySourceValue = Strings.Common_Checking;
    [ObservableProperty] private string _keySourceDetail = "";
    [ObservableProperty] private string? _keySourceToolTip;
    [ObservableProperty] private StatusKind _keySourceIndicator = StatusKind.Busy;

    // 已授权客户端
    [ObservableProperty] private int? _authorizedClients;
    [ObservableProperty] private string _clientsValue = Strings.Common_Checking;
    [ObservableProperty] private string _clientsDetail = "";
    [ObservableProperty] private string _clientsActionText = Strings.Ov_ManageClients;
    [ObservableProperty] private string? _clientsToolTip;

    [ObservableProperty] private bool _isLoading = true;

    /// <summary>首页「网络地址」卡片；端口每次刷新时从 config.json 读取</summary>
    public NetworkAddressesViewModel Network { get; }

    public OverviewViewModel(ServiceManager serviceManager, PipeClient pipeClient, DialogService dialogService,
        Action<Type> navigate)
    {
        _serviceManager = serviceManager;
        _pipeClient = pipeClient;
        _dialogService = dialogService;
        _navigate = navigate;
        Network = new NetworkAddressesViewModel(dialogService, () => ServerConfig.Load().Port,
            Strings.Ov_NetFootnote,
            Strings.Ov_NetDetail);
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        await Task.WhenAll(RefreshAsync(), Network.ActivateAsync());
        IsLoading = false;
    }

    /// <summary>页面卸载：取消进行中的刷新，结果不再写回已失效的视图</summary>
    public void Deactivate()
    {
        _refreshCts?.Cancel();
        Network.Deactivate();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshStatusAsync() => RefreshAsync();

    private bool CanRefresh() => !IsServiceBusy;

    /// <summary>依次查询 SCM、管理通道；新的刷新开始时丢弃旧的结果</summary>
    private async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        var snapshot = await QueryAsync();
        if (!cts.IsCancellationRequested)
            Apply(snapshot);
    }

    private async Task<ServerOverviewSnapshot> QueryAsync()
    {
        var service = await _serviceManager.GetStateAsync();
        if (!service.IsRunning)
        {
            var state = service.IsPending ? KeySourceState.ServicePending : KeySourceState.ServiceNotRunning;
            return new ServerOverviewSnapshot(service, state, null, null, null, null);
        }

        KeySourceStatus? status = null;
        string? keyError = null;
        KeySourceState keyState;
        try
        {
            status = await _pipeClient.SendCommandAsync<KeySourceStatus>("GetKeySourceStatus");
            keyState = MapKeySource(status);
        }
        catch (PipeConnectionException ex)
        {
            keyState = ex.AccessDenied ? KeySourceState.ChannelAccessDenied : KeySourceState.ChannelUnavailable;
            keyError = ex.Message;
        }
        catch (Exception ex)
        {
            keyState = KeySourceState.QueryFailed;
            keyError = ex.Message;
        }

        int? clients = null;
        string? clientError = null;
        try
        {
            clients = (await _pipeClient.SendCommandAsync<ClientDto[]>("GetClients"))?.Length ?? 0;
        }
        catch (Exception ex)
        {
            clientError = ex.Message;
        }
        return new ServerOverviewSnapshot(service, keyState, status, keyError, clients, clientError);
    }

    /// <summary>
    /// 服务回复的密钥源状态 → 卡片状态。服务端规则见 KeySourceManager.GetStatus：
    /// 没有分享的证书时 Thumbprint 为空；默认证书是测试证书或加载失败时 Mode 为 test
    /// </summary>
    public static KeySourceState MapKeySource(KeySourceStatus? status) => status switch
    {
        null => KeySourceState.QueryFailed,
        { Thumbprint: null or "" } => KeySourceState.NotConfigured,
        { Mode: KeySourceManager.ModeProduction } => KeySourceState.Ready,
        _ => KeySourceState.NotReady
    };

    /// <summary>把一次查询结果映射到三张卡片；只描述真实查询到的内容</summary>
    public void Apply(ServerOverviewSnapshot s)
    {
        var service = s.Service;
        ServiceKind = service.Kind;
        ServiceInstalled = service.IsInstalled;
        ServiceRunning = service.IsRunning;
        CanInstallService = service.Kind == ServiceStatusKind.NotInstalled;
        CanStartService = service.Kind is ServiceStatusKind.Stopped or ServiceStatusKind.Paused;
        CanStopService = service.Kind == ServiceStatusKind.Running;
        (ServiceValue, ServiceDetail, ServiceIndicator) = service.Kind switch
        {
            ServiceStatusKind.Running => (Strings.Svc_Running, Strings.Svc_RunningDetail, StatusKind.Success),
            ServiceStatusKind.Stopped => (Strings.Svc_Stopped, Strings.Svc_StoppedDetail, StatusKind.Caution),
            ServiceStatusKind.Paused => (Strings.Svc_Paused, Strings.Svc_PausedDetail, StatusKind.Caution),
            ServiceStatusKind.Starting => (Strings.Svc_Starting, Strings.Svc_StartingDetail, StatusKind.Busy),
            ServiceStatusKind.Stopping => (Strings.Svc_Stopping, Strings.Svc_StoppingDetail, StatusKind.Busy),
            ServiceStatusKind.NotInstalled => (Strings.Svc_NotInstalled, Strings.Svc_NotInstalledDetail, StatusKind.Neutral),
            ServiceStatusKind.QueryFailed => (Strings.Common_QueryFailed, service.Error ?? Strings.Svc_QueryFailedDetail, StatusKind.Critical),
            _ => (Strings.Svc_Unknown, Strings.Svc_UnknownDetail, StatusKind.Caution)
        };
        RefreshServiceCommands();

        KeySourceState = s.KeySource;
        KeySourceToolTip = s.KeySourceError ?? s.KeySourceStatus?.Detail;
        (KeySourceValue, KeySourceDetail, KeySourceIndicator) = s.KeySource switch
        {
            KeySourceState.Ready => (Strings.Ks_Ready, s.KeySourceStatus?.Detail ?? "", StatusKind.Success),
            KeySourceState.NotReady => (Strings.Ks_NotReady, s.KeySourceStatus?.Detail ?? "", StatusKind.Caution),
            KeySourceState.NotConfigured => (Strings.Ks_NotConfigured, Strings.Ks_NotConfiguredDetail, StatusKind.Caution),
            KeySourceState.ServiceNotRunning => (Strings.Ks_ServiceNotRunning, Strings.Ks_ServiceNotRunningDetail, StatusKind.Neutral),
            KeySourceState.ServicePending => (Strings.Ks_WaitingForService, Strings.Ks_WaitingForServiceDetail, StatusKind.Busy),
            KeySourceState.ChannelUnavailable => (Strings.Ks_StatusUnavailable, Strings.Ks_ChannelUnavailable, StatusKind.Caution),
            KeySourceState.ChannelAccessDenied => (Strings.Ks_StatusUnavailable, Strings.Ks_ChannelAccessDenied, StatusKind.Critical),
            KeySourceState.QueryFailed => (Strings.Common_QueryFailed, Strings.Ks_QueryFailedDetail, StatusKind.Critical),
            _ => (Strings.Common_Checking, "", StatusKind.Busy)
        };

        AuthorizedClients = s.ClientCount;
        // 原始错误放在 ToolTip，卡片上只显示简短说明
        ClientsToolTip = s.ClientError;
        (ClientsValue, ClientsDetail) = (s.ClientCount, s.ClientError, service.IsRunning) switch
        {
            (null, _, false) => ("—", Strings.Cl_ShownWhenRunning),
            (null, _, _) when s.KeySource is KeySourceState.ChannelUnavailable => (Strings.Cl_Unavailable, Strings.Ks_ChannelUnavailable),
            (null, _, _) when s.KeySource is KeySourceState.ChannelAccessDenied => (Strings.Cl_Unavailable, Strings.Ks_ChannelAccessDenied),
            (null, _, _) => (Strings.Cl_Unavailable, Strings.Cl_QueryFailedDetail),
            (0, _, _) => (Strings.Cl_None, Strings.Cl_NoneDetail),
            ({ } n, _, _) => (string.Format(Strings.Cl_Count, n), Strings.Cl_CountDetail),
        };
        ClientsActionText = s.ClientCount == 0 ? Strings.Ov_ViewPairingRequests : Strings.Ov_ManageClients;
    }

    private void RefreshServiceCommands()
    {
        InstallServiceCommand.NotifyCanExecuteChanged();
        StartServiceCommand.NotifyCanExecuteChanged();
        StopServiceCommand.NotifyCanExecuteChanged();
        UninstallServiceCommand.NotifyCanExecuteChanged();
    }

    private bool CanInstall() => !IsServiceBusy && CanInstallService;
    private bool CanStart() => !IsServiceBusy && CanStartService;
    private bool CanStop() => !IsServiceBusy && CanStopService;
    private bool CanUninstall() => !IsServiceBusy && ServiceInstalled;

    /// <summary>执行一个服务操作：期间显示进行中状态，结束后按 SCM 实际状态刷新</summary>
    private async Task RunServiceActionAsync(Func<Task> action, string pendingText)
    {
        IsServiceBusy = true;
        ServiceValue = pendingText;
        ServiceIndicator = StatusKind.Busy;
        try
        {
            await action();
        }
        finally
        {
            IsServiceBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>轮询 SCM 直到进入目标状态或超时</summary>
    private async Task<bool> WaitForAsync(ServiceStatusKind target, int attempts = 20)
    {
        for (var i = 0; i < attempts; i++)
        {
            if ((await _serviceManager.GetStateAsync()).Kind == target)
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallServiceAsync() => RunServiceActionAsync(async () =>
    {
        try
        {
            await _serviceManager.InstallAsync();
            await _dialogService.SuccessAsync(Strings.Ov_InstalledTitle, Strings.Ov_Installed);
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_InstallFailed, ex.Message);
        }
    }, Strings.Common_Installing);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartServiceAsync() => RunServiceActionAsync(async () =>
    {
        try
        {
            await _serviceManager.StartAsync();
            if (!await WaitForAsync(ServiceStatusKind.Running))
                await _dialogService.WarningAsync(Strings.Ov_StartTimeoutTitle, Strings.Ov_StartTimeout);
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_StartFailed, ex.Message);
        }
    }, Strings.Svc_Starting);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopServiceAsync() => RunServiceActionAsync(async () =>
    {
        try
        {
            await _serviceManager.StopAsync();
            if (!await WaitForAsync(ServiceStatusKind.Stopped))
                await _dialogService.WarningAsync(Strings.Ov_StopTimeoutTitle, Strings.Ov_StopTimeout);
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_StopFailed, ex.Message);
        }
    }, Strings.Svc_Stopping);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallServiceAsync()
    {
        if (await ServiceUninstallUi.ConfirmAsync(_dialogService) is not { } removeData)
            return;

        await RunServiceActionAsync(async () =>
        {
            try
            {
                var result = await _serviceManager.UninstallAsync(removeData);
                await ServiceUninstallUi.ShowResultAsync(_dialogService, result);
            }
            catch (Exception ex)
            {
                await _dialogService.ErrorAsync(Strings.Common_UninstallFailed, ex.Message);
            }
        }, Strings.Common_Uninstalling);
    }

    [RelayCommand]
    private void OpenCertificates() => _navigate(typeof(Views.Pages.CertificatesPage));

    /// <summary>没有客户端时去配对请求页，否则去已配对客户端页</summary>
    [RelayCommand]
    private void OpenClients() => _navigate(AuthorizedClients == 0
        ? typeof(Views.Pages.PairingRequestsPage)
        : typeof(Views.Pages.ClientsPage));
}

public record ClientDto(string Id, string MachineName, string IpAddress, DateTime PairedAt, DateTime LastSeen,
    List<string>? AllowedCertIds);
