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
    [ObservableProperty] private string _serviceValue = "检查中…";
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
    [ObservableProperty] private string _keySourceValue = "检查中…";
    [ObservableProperty] private string _keySourceDetail = "";
    [ObservableProperty] private string? _keySourceToolTip;
    [ObservableProperty] private StatusKind _keySourceIndicator = StatusKind.Busy;

    // 已授权客户端
    [ObservableProperty] private int? _authorizedClients;
    [ObservableProperty] private string _clientsValue = "检查中…";
    [ObservableProperty] private string _clientsDetail = "";
    [ObservableProperty] private string _clientsActionText = "管理客户端";
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
            "客户端可尝试用这些地址连接本机",
            "这里列出本机网卡上的地址，以及按服务端口生成的 https 连接地址。\n\n" +
            "• 客户端能否连上，取决于服务是否在监听、网络路由和防火墙；复制连接地址不代表已验证远程可达。\n" +
            "• 服务端 TLS 证书是自签名的，不包含 IP；客户端按配对时记录的证书指纹校验，而不是按地址校验。\n" +
            "• 环回地址只能在本机使用；链路本地 IPv6 地址带本机接口编号，不提供连接地址复制。");
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
            ServiceStatusKind.Running => ("运行中", "签名服务进程正在运行（Windows 服务名：RemoteKSP）", StatusKind.Success),
            ServiceStatusKind.Stopped => ("已停止", "服务停止期间，客户端无法签名", StatusKind.Caution),
            ServiceStatusKind.Paused => ("已暂停", "服务暂停期间，客户端无法签名", StatusKind.Caution),
            ServiceStatusKind.Starting => ("启动中…", "正在等待服务启动", StatusKind.Busy),
            ServiceStatusKind.Stopping => ("停止中…", "正在等待服务停止", StatusKind.Busy),
            ServiceStatusKind.NotInstalled => ("未安装", "安装后签名服务会随系统自动启动", StatusKind.Neutral),
            ServiceStatusKind.QueryFailed => ("查询失败", service.Error ?? "无法读取服务状态", StatusKind.Critical),
            _ => ("状态未知", "Service Control Manager 返回了未识别的状态", StatusKind.Caution)
        };
        RefreshServiceCommands();

        KeySourceState = s.KeySource;
        KeySourceToolTip = s.KeySourceError ?? s.KeySourceStatus?.Detail;
        (KeySourceValue, KeySourceDetail, KeySourceIndicator) = s.KeySource switch
        {
            KeySourceState.Ready => ("已就绪", s.KeySourceStatus?.Detail ?? "", StatusKind.Success),
            KeySourceState.NotReady => ("未就绪", s.KeySourceStatus?.Detail ?? "", StatusKind.Caution),
            KeySourceState.NotConfigured => ("未配置", "没有分享给客户端的证书，请在证书管理中添加", StatusKind.Caution),
            KeySourceState.ServiceNotRunning => ("服务未运行", "启动 Windows 服务后才能查询密钥源", StatusKind.Neutral),
            KeySourceState.ServicePending => ("等待服务", "服务状态变化后会重新查询", StatusKind.Busy),
            KeySourceState.ChannelUnavailable => ("无法获取密钥源状态", "管理通道暂时不可连接，请稍后刷新", StatusKind.Caution),
            KeySourceState.ChannelAccessDenied => ("无法获取密钥源状态", "无权访问管理通道，请以管理员身份运行", StatusKind.Critical),
            KeySourceState.QueryFailed => ("查询失败", "服务处理状态查询时出错，悬停查看详情", StatusKind.Critical),
            _ => ("检查中…", "", StatusKind.Busy)
        };

        AuthorizedClients = s.ClientCount;
        // 原始错误放在 ToolTip，卡片上只显示简短说明
        ClientsToolTip = s.ClientError;
        (ClientsValue, ClientsDetail) = (s.ClientCount, s.ClientError, service.IsRunning) switch
        {
            (null, _, false) => ("—", "服务运行后显示"),
            (null, _, _) when s.KeySource is KeySourceState.ChannelUnavailable => ("无法获取", "管理通道暂时不可连接，请稍后刷新"),
            (null, _, _) when s.KeySource is KeySourceState.ChannelAccessDenied => ("无法获取", "无权访问管理通道，请以管理员身份运行"),
            (null, _, _) => ("无法获取", "服务处理查询时出错，悬停查看详情"),
            (0, _, _) => ("暂无已授权客户端", "客户端配对后在这里显示"),
            ({ } n, _, _) => ($"{n} 台", "已配对并持有有效凭据的客户端"),
        };
        ClientsActionText = s.ClientCount == 0 ? "查看配对请求" : "管理客户端";
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
            await _dialogService.SuccessAsync("安装成功", "Remote KSP 服务已成功安装");
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("安装失败", ex.Message);
        }
    }, "安装中…");

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartServiceAsync() => RunServiceActionAsync(async () =>
    {
        try
        {
            await _serviceManager.StartAsync();
            if (!await WaitForAsync(ServiceStatusKind.Running))
                await _dialogService.WarningAsync("启动超时", "服务启动命令已发送，但状态未及时更新，请稍后刷新状态");
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("启动失败", ex.Message);
        }
    }, "启动中…");

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopServiceAsync() => RunServiceActionAsync(async () =>
    {
        try
        {
            await _serviceManager.StopAsync();
            if (!await WaitForAsync(ServiceStatusKind.Stopped))
                await _dialogService.WarningAsync("停止超时", "服务停止命令已发送，但状态未及时更新，请稍后刷新状态");
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("停止失败", ex.Message);
        }
    }, "停止中…");

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
                await _dialogService.ErrorAsync("卸载失败", ex.Message);
            }
        }, "卸载中…");
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
