using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Server.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly DialogService _dialogService;
    private readonly Services.ServiceManager _serviceManager;

    [ObservableProperty] private string _version = typeof(SettingsViewModel).Assembly.GetName().Version!.ToString(3);

    public RemoteKSP.Shared.Localization.LanguageSettingsViewModel Language { get; } = new();
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private string _serviceStatus = Strings.Common_CheckingDots;
    [ObservableProperty] private bool _serviceInstalled;
    [ObservableProperty] private bool _serviceRunning;
    [ObservableProperty] private string _tlsThumbprint = Strings.Common_Reading;
    [ObservableProperty] private string _tlsNotAfter = "";

    private readonly Services.PipeClient _pipeClient;

    public SettingsViewModel(DialogService dialogService, Services.ServiceManager serviceManager,
        Services.PipeClient pipeClient)
    {
        _dialogService = dialogService;
        _serviceManager = serviceManager;
        _pipeClient = pipeClient;
        DataDirectory = Core.ServerPaths.DataDir;
    }

    public async Task InitializeAsync()
    {
        LoadPort();
        await RefreshServiceStatusAsync();
        await RefreshProgramAsync();
        await LoadTlsCertificateAsync();
    }

    private record TlsInfo(string? Thumbprint, DateTime? NotAfter, int PairedClients);

    /// <summary>每 4 位一组，方便和客户端配对时显示的指纹核对</summary>
    private static string Grouped(string thumbprint) =>
        string.Join(" ", Enumerable.Range(0, (thumbprint.Length + 3) / 4)
            .Select(i => thumbprint.Substring(i * 4, Math.Min(4, thumbprint.Length - i * 4))));

    private async Task<TlsInfo?> LoadTlsCertificateAsync()
    {
        try
        {
            var info = await _pipeClient.SendCommandAsync<TlsInfo>("GetTlsCertificate");
            TlsThumbprint = string.IsNullOrEmpty(info?.Thumbprint) ? Strings.Set_TlsNotLoaded : Grouped(info.Thumbprint);
            TlsNotAfter = info?.NotAfter is { } notAfter ? notAfter.ToLocalTime().ToString("yyyy-MM-dd") : "";
            return info;
        }
        catch (Services.PipeConnectionException)
        {
            TlsThumbprint = Strings.Set_TlsServiceNotRunning;
            TlsNotAfter = "";
            return null;
        }
    }

    /// <summary>
    /// 重新生成 server.pfx。服务运行时由服务生成并立即生效；服务连不上（例如证书损坏导致起不来）时，
    /// 先停止服务，由本进程（管理员）生成，再启动服务
    /// </summary>
    [RelayCommand]
    private async Task RegenerateTlsCertificateAsync()
    {
        var info = await LoadTlsCertificateAsync();
        var clients = info == null ? Strings.Set_AllPairedClients : string.Format(Strings.Set_NPairedClients, info.PairedClients);
        var confirmed = await _dialogService.ConfirmAsync(
            Strings.Set_RegenTitle,
            string.Format(Strings.Set_RegenMessage, clients),
            Strings.Set_Regenerate,
            destructive: true);
        if (!confirmed)
            return;

        try
        {
            Core.TlsCertificateManager.RegenerateResult result;
            try
            {
                result = await _pipeClient.SendCommandAsync<Core.TlsCertificateManager.RegenerateResult>(
                    "RegenerateTlsCertificate") ?? throw new InvalidOperationException(Strings.Add_NoResult);
            }
            catch (Services.PipeConnectionException)
            {
                result = await RegenerateWithServiceStoppedAsync();
            }

            await LoadTlsCertificateAsync();
            await RefreshServiceStatusAsync();
            await _dialogService.SuccessAsync(Strings.Set_RegeneratedTitle,
                Strings.Set_Regenerated,
                new DialogField(Strings.Set_NewThumbprint, Grouped(result.NewThumbprint), Monospace: true),
                new DialogField(Strings.Set_OldThumbprint, result.OldThumbprint is { } old ? Grouped(old) : Strings.Set_OldUnreadable, Monospace: true),
                new DialogField(Strings.Set_Backup, result.BackupFile ?? Strings.Set_NoOldFile, Monospace: true));
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Set_RegenFailed, ex.Message);
        }
    }

    /// <summary>服务连不上：停止服务（可能在反复重启），本进程生成证书，再启动服务</summary>
    private async Task<Core.TlsCertificateManager.RegenerateResult> RegenerateWithServiceStoppedAsync()
    {
        var restart = false;
        await Task.Run(() =>
        {
            using var sc = new ServiceController("RemoteKSP");
            ServiceControllerStatus status;
            try
            {
                status = sc.Status; // 服务未安装时这里才抛
            }
            catch (InvalidOperationException)
            {
                return; // 服务未安装：直接生成，不用启动
            }

            restart = true;
            if (status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        });

        var result = await Task.Run(Core.TlsCertificateManager.RegenerateServerCertificate);

        if (restart)
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController("RemoteKSP");
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            });
        }
        return result;
    }

    [RelayCommand]
    private async Task RefreshServiceStatusAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController("RemoteKSP");
                ServiceInstalled = true;
                ServiceRunning = sc.Status == ServiceControllerStatus.Running;
                ServiceStatus = sc.Status switch
                {
                    ServiceControllerStatus.Running => Strings.Svc_Running,
                    ServiceControllerStatus.Stopped => Strings.Svc_Stopped,
                    ServiceControllerStatus.StartPending => Strings.Set_StartPending,
                    ServiceControllerStatus.StopPending => Strings.Set_StopPending,
                    _ => sc.Status.ToString()
                };
            }
            catch
            {
                ServiceInstalled = false;
                ServiceRunning = false;
                ServiceStatus = Strings.Svc_NotInstalled;
            }
        });
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        try
        {
            using var sc = new ServiceController("RemoteKSP");
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, System.TimeSpan.FromSeconds(30));
                await _dialogService.SuccessAsync(Strings.Set_StoppedTitle, Strings.Set_Stopped);
            }
            await RefreshServiceStatusAsync();
        }
        catch (System.Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_StopFailed, ex.Message);
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        try
        {
            using var sc = new ServiceController("RemoteKSP");
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, System.TimeSpan.FromSeconds(30));
                await _dialogService.SuccessAsync(Strings.Set_StartedTitle, Strings.Set_Started);
            }
            await RefreshServiceStatusAsync();
        }
        catch (System.Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_StartFailed, ex.Message);
        }
    }

    // ===== 服务程序（%ProgramFiles%\RemoteKSP） =====

    [ObservableProperty] private string _programPath = "";
    [ObservableProperty] private string _programStatus = "";
    [ObservableProperty] private bool _programNeedsAction;
    [ObservableProperty] private string _programActionText = Strings.Set_UpdateProgram;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    /// <summary>服务注册的 exe 在哪里、是否在 Program Files、是否和当前打开的这个版本一致（比较大文件，放后台线程）</summary>
    private async Task RefreshProgramAsync()
    {
        var (path, status, needsAction, action) = await Task.Run(() =>
        {
            var registered = Services.ServiceManager.RegisteredExecutable();
            if (registered == null)
                return ("", string.Format(Strings.Set_ProgramNotInstalled, Services.ServiceProgram.InstallDir), false, "");
            if (!System.IO.File.Exists(registered))
                return (registered, Strings.Set_ProgramMissing, true, Strings.Set_RepairProgram);

            var inProgramFiles = string.Equals(System.IO.Path.GetFullPath(registered),
                System.IO.Path.GetFullPath(Services.ServiceProgram.InstalledExe), StringComparison.OrdinalIgnoreCase);
            if (!inProgramFiles)
            {
                var writable = Services.ServiceProgram.WritableByNonAdmins(new System.IO.FileInfo(registered).Directory!);
                return (registered, writable
                        ? Strings.Set_ProgramUserWritable
                        : Strings.Set_ProgramNotInProgramFiles,
                    true, Strings.Set_MoveToProgramFiles);
            }

            var installedVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(registered).FileVersion ?? Strings.Common_Unknown;
            var current = Environment.ProcessPath;
            if (current == null || Services.ServiceProgram.SameBuild(registered, current))
                return (registered, string.Format(Strings.Set_ProgramSameVersion, installedVersion), false, "");
            return (registered, string.Format(Strings.Set_ProgramOtherVersion, installedVersion, Version), true, Strings.Set_UpdateProgram);
        });
        ProgramPath = path;
        ProgramStatus = status;
        ProgramNeedsAction = needsAction;
        ProgramActionText = action;
    }

    /// <summary>执行一个服务操作：期间禁用按钮并显示进行中，结束后刷新状态</summary>
    private async Task RunBusyAsync(string text, Func<Task> action)
    {
        IsBusy = true;
        BusyText = text;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            await RefreshServiceStatusAsync();
            await RefreshProgramAsync();
            await LoadTlsCertificateAsync();
        }
    }

    [RelayCommand]
    private Task InstallServiceAsync() => RunBusyAsync(Strings.Set_InstallingBusy, async () =>
    {
        try
        {
            await _serviceManager.InstallAsync();
            await _dialogService.SuccessAsync(Strings.Set_InstalledTitle,
                string.Format(Strings.Set_Installed, Services.ServiceProgram.InstallDir));
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_InstallFailed, ex.Message);
        }
    });

    /// <summary>升级或迁移：把当前打开的这个 exe 部署到 Program Files，服务改为指向它</summary>
    [RelayCommand]
    private async Task UpdateProgramAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            ProgramActionText,
            string.Format(Strings.Set_UpdateMessage, Services.ServiceProgram.InstallDir),
            ProgramActionText);
        if (!confirmed)
            return;

        await RunBusyAsync(Strings.Set_UpdatingBusy, async () =>
        {
            try
            {
                var oldPath = ProgramPath;
                await _serviceManager.UpdateProgramAsync();
                var moved = !string.Equals(oldPath, Services.ServiceProgram.InstalledExe, StringComparison.OrdinalIgnoreCase);
                await _dialogService.SuccessAsync(Strings.Set_UpdatedTitle,
                    moved
                        ? string.Format(Strings.Set_UpdatedMoved, Services.ServiceProgram.InstalledExe, oldPath)
                        : string.Format(Strings.Set_UpdatedVersion, Version));
            }
            catch (Exception ex)
            {
                await _dialogService.ErrorAsync(Strings.Set_UpdateFailed, ex.Message);
            }
        });
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (await ServiceUninstallUi.ConfirmAsync(_dialogService) is not { } removeData)
            return;

        await RunBusyAsync(Strings.Set_UninstallingBusy, async () =>
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
        });
    }

    [RelayCommand]
    private async Task ReinstallServiceAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            Strings.Set_ReinstallTitle,
            Strings.Set_ReinstallMessage,
            Strings.Set_Reinstall);
        if (!confirmed)
            return;

        await RunBusyAsync(Strings.Set_ReinstallingBusy, async () =>
        {
            try
            {
                // UninstallAsync 会先停止服务：运行中的服务被 delete 只会「标记为删除」，紧接着 create 会失败。
                // 服务程序不删：马上又要部署到同一个位置
                if (ServiceInstalled)
                {
                    await _serviceManager.UninstallAsync(removeData: false, removeProgram: false);
                    await Task.Delay(500); // 等待 SCM 更新状态
                }
                await _serviceManager.InstallAsync();
                await _dialogService.SuccessAsync(Strings.Set_ReinstalledTitle, Strings.Set_Reinstalled);
            }
            catch (Exception ex)
            {
                await _dialogService.ErrorAsync(Strings.Set_ReinstallFailed, ex.Message);
            }
        });
    }

    // ===== HTTPS 端口 =====

    [ObservableProperty] private string _httpsPort = "";
    private int _currentPort;

    private void LoadPort()
    {
        _currentPort = Core.ServerConfig.Load().Port;
        HttpsPort = _currentPort.ToString();
    }

    [RelayCommand]
    private async Task ApplyPortAsync()
    {
        if (!int.TryParse(HttpsPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            await _dialogService.WarningAsync(Strings.Set_PortInvalidTitle, Strings.Set_PortInvalid);
            return;
        }
        if (port == _currentPort)
            return;
        if (Services.ServiceManager.IsPortInUse(port))
        {
            await _dialogService.WarningAsync(Strings.Set_PortInUseTitle, string.Format(Strings.Set_PortInUse, port));
            return;
        }

        var info = await LoadTlsCertificateAsync();
        var clients = info == null ? Strings.Set_AllPairedClients : string.Format(Strings.Set_NPairedClients, info.PairedClients);
        var confirmed = await _dialogService.ConfirmAsync(
            Strings.Set_PortTitle,
            string.Format(Strings.Set_PortMessage, _currentPort, port, clients),
            Strings.Set_ChangePort,
            destructive: true);
        if (!confirmed)
            return;

        await RunBusyAsync(string.Format(Strings.Set_ChangingPort, port), async () =>
        {
            try
            {
                var firewallOk = await _serviceManager.ChangePortAsync(port);
                LoadPort();
                if (firewallOk)
                    await _dialogService.SuccessAsync(Strings.Set_PortChangedTitle, string.Format(Strings.Set_PortChanged, port));
                else
                    await _dialogService.WarningAsync(Strings.Set_PortChangedNoFirewallTitle,
                        string.Format(Strings.Set_PortChangedNoFirewall, port));
            }
            catch (Exception ex)
            {
                LoadPort();
                await _dialogService.ErrorAsync(Strings.Set_ChangePortFailed, ex.Message);
            }
        });
    }
}
