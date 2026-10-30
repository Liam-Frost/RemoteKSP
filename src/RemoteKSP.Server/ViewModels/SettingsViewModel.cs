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
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private string _serviceStatus = "检查中...";
    [ObservableProperty] private bool _serviceInstalled;
    [ObservableProperty] private bool _serviceRunning;
    [ObservableProperty] private string _tlsThumbprint = "读取中...";
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
            TlsThumbprint = string.IsNullOrEmpty(info?.Thumbprint) ? "服务尚未加载 TLS 证书" : Grouped(info.Thumbprint);
            TlsNotAfter = info?.NotAfter is { } notAfter ? notAfter.ToLocalTime().ToString("yyyy-MM-dd") : "";
            return info;
        }
        catch (Services.PipeConnectionException)
        {
            TlsThumbprint = "服务未运行，无法读取";
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
        var clients = info == null ? "所有" : $"全部 {info.PairedClients} 个";
        var confirmed = await _dialogService.ConfirmAsync(
            "重新生成 TLS 证书",
            "将为 HTTPS 服务生成一张新的自签名证书。\n\n" +
            $"客户端在配对时记录了旧证书的指纹，生成后{clients}已配对客户端都会连接失败，" +
            "需要在每台客户端上移除该服务端并重新配对。\n\n" +
            "原 server.pfx 会备份到数据目录，不会删除。只在证书损坏、服务因证书无法启动等情况下使用。",
            "重新生成",
            destructive: true);
        if (!confirmed)
            return;

        try
        {
            Core.TlsCertificateManager.RegenerateResult result;
            try
            {
                result = await _pipeClient.SendCommandAsync<Core.TlsCertificateManager.RegenerateResult>(
                    "RegenerateTlsCertificate") ?? throw new InvalidOperationException("服务未返回结果");
            }
            catch (Services.PipeConnectionException)
            {
                result = await RegenerateWithServiceStoppedAsync();
            }

            await LoadTlsCertificateAsync();
            await RefreshServiceStatusAsync();
            await _dialogService.SuccessAsync("已重新生成 TLS 证书",
                "新证书已生效。请在每台客户端上移除该服务端并重新配对，配对时核对验证码。",
                new DialogField("新指纹", Grouped(result.NewThumbprint), Monospace: true),
                new DialogField("旧指纹", result.OldThumbprint is { } old ? Grouped(old) : "（原证书无法读取）", Monospace: true),
                new DialogField("原证书备份", result.BackupFile ?? "（原来没有证书文件）", Monospace: true));
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("重新生成 TLS 证书失败", ex.Message);
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
                    ServiceControllerStatus.Running => "运行中",
                    ServiceControllerStatus.Stopped => "已停止",
                    ServiceControllerStatus.StartPending => "正在启动...",
                    ServiceControllerStatus.StopPending => "正在停止...",
                    _ => sc.Status.ToString()
                };
            }
            catch
            {
                ServiceInstalled = false;
                ServiceRunning = false;
                ServiceStatus = "未安装";
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
                await _dialogService.SuccessAsync("服务已停止", "RemoteKSP 服务已成功停止。");
            }
            await RefreshServiceStatusAsync();
        }
        catch (System.Exception ex)
        {
            await _dialogService.ErrorAsync("停止失败", ex.Message);
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
                await _dialogService.SuccessAsync("服务已启动", "RemoteKSP 服务已成功启动。");
            }
            await RefreshServiceStatusAsync();
        }
        catch (System.Exception ex)
        {
            await _dialogService.ErrorAsync("启动失败", ex.Message);
        }
    }

    // ===== 服务程序（%ProgramFiles%\RemoteKSP） =====

    [ObservableProperty] private string _programPath = "";
    [ObservableProperty] private string _programStatus = "";
    [ObservableProperty] private bool _programNeedsAction;
    [ObservableProperty] private string _programActionText = "更新服务程序";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    /// <summary>服务注册的 exe 在哪里、是否在 Program Files、是否和当前打开的这个版本一致（比较大文件，放后台线程）</summary>
    private async Task RefreshProgramAsync()
    {
        var (path, status, needsAction, action) = await Task.Run(() =>
        {
            var registered = Services.ServiceManager.RegisteredExecutable();
            if (registered == null)
                return ("", $"未安装。安装时会把服务程序复制到 {Services.ServiceProgram.InstallDir}", false, "");
            if (!System.IO.File.Exists(registered))
                return (registered, "服务程序文件不存在，服务无法启动", true, "修复服务程序");

            var inProgramFiles = string.Equals(System.IO.Path.GetFullPath(registered),
                System.IO.Path.GetFullPath(Services.ServiceProgram.InstalledExe), StringComparison.OrdinalIgnoreCase);
            if (!inProgramFiles)
            {
                var writable = Services.ServiceProgram.WritableByNonAdmins(new System.IO.FileInfo(registered).Directory!);
                return (registered, writable
                        ? "服务程序所在目录普通用户可修改，而服务以 SYSTEM 运行。建议迁移到 Program Files"
                        : "服务程序不在 Program Files 中。建议迁移，之后升级只需在这里点「更新服务程序」",
                    true, "迁移到 Program Files");
            }

            var installedVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(registered).FileVersion ?? "未知";
            var current = Environment.ProcessPath;
            if (current == null || Services.ServiceProgram.SameBuild(registered, current))
                return (registered, $"与当前程序版本一致（{installedVersion}）", false, "");
            return (registered, $"已安装版本 {installedVersion}，与当前打开的程序（{Version}）不一致", true, "更新服务程序");
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
    private Task InstallServiceAsync() => RunBusyAsync("正在安装服务（复制服务程序到 Program Files）…", async () =>
    {
        try
        {
            await _serviceManager.InstallAsync();
            await _dialogService.SuccessAsync("安装完成",
                $"服务程序已复制到 {Services.ServiceProgram.InstallDir}，RemoteKSP 服务已安装并设为开机自动启动。");
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("安装失败", ex.Message);
        }
    });

    /// <summary>升级或迁移：把当前打开的这个 exe 部署到 Program Files，服务改为指向它</summary>
    [RelayCommand]
    private async Task UpdateProgramAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            ProgramActionText,
            $"将停止服务，把当前打开的程序复制到 {Services.ServiceProgram.InstallDir}，服务改为使用这个副本，原来在运行就重新启动。\n\n" +
            "数据目录、证书、PIN 和客户端配对都不受影响，客户端不需要重新配对。",
            ProgramActionText);
        if (!confirmed)
            return;

        await RunBusyAsync("正在更新服务程序…", async () =>
        {
            try
            {
                var oldPath = ProgramPath;
                await _serviceManager.UpdateProgramAsync();
                var moved = !string.Equals(oldPath, Services.ServiceProgram.InstalledExe, StringComparison.OrdinalIgnoreCase);
                await _dialogService.SuccessAsync("服务程序已更新",
                    moved
                        ? $"服务现在使用 {Services.ServiceProgram.InstalledExe}。原来的 {oldPath} 不再被服务使用，可以删除。"
                        : $"服务已更新为当前版本（{Version}）。");
            }
            catch (Exception ex)
            {
                await _dialogService.ErrorAsync("更新服务程序失败", ex.Message);
            }
        });
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (await ServiceUninstallUi.ConfirmAsync(_dialogService) is not { } removeData)
            return;

        await RunBusyAsync("正在卸载服务…", async () =>
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
        });
    }

    [RelayCommand]
    private async Task ReinstallServiceAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "重新安装服务",
            "将删除并重新创建 RemoteKSP 服务（服务程序同时更新为当前打开的版本），服务会停止。\n\n" +
            "数据目录和配置不受影响。服务注册信息损坏时使用；只是升级版本请用「更新服务程序」。",
            "重新安装");
        if (!confirmed)
            return;

        await RunBusyAsync("正在重新安装服务…", async () =>
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
                await _dialogService.SuccessAsync("重新安装完成", "RemoteKSP 服务已重新安装。");
            }
            catch (Exception ex)
            {
                await _dialogService.ErrorAsync("重新安装失败", ex.Message);
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
            await _dialogService.WarningAsync("端口无效", "请输入 1–65535 之间的端口号。");
            return;
        }
        if (port == _currentPort)
            return;
        if (Services.ServiceManager.IsPortInUse(port))
        {
            await _dialogService.WarningAsync("端口已被占用", $"本机已有程序在监听 {port} 端口，请换一个。");
            return;
        }

        var info = await LoadTlsCertificateAsync();
        var clients = info == null ? "所有" : $"全部 {info.PairedClients} 个";
        var confirmed = await _dialogService.ConfirmAsync(
            "修改 HTTPS 端口",
            $"端口将从 {_currentPort} 改为 {port}，服务在运行时会重启，防火墙规则一并更新。\n\n" +
            $"客户端保存的服务端地址包含端口，修改后{clients}已配对客户端都会连接失败，" +
            $"需要在每台客户端上移除该服务端，再用新端口 {port} 重新配对。",
            "修改端口",
            destructive: true);
        if (!confirmed)
            return;

        await RunBusyAsync($"正在把端口改为 {port}…", async () =>
        {
            try
            {
                var firewallOk = await _serviceManager.ChangePortAsync(port);
                LoadPort();
                if (firewallOk)
                    await _dialogService.SuccessAsync("端口已修改", $"HTTPS 端口已改为 {port}。请让各客户端用新端口重新配对。");
                else
                    await _dialogService.WarningAsync("端口已修改，防火墙规则未更新",
                        $"HTTPS 端口已改为 {port}，但添加防火墙规则失败（可能由组策略管理）。请手动放行 TCP {port} 入站。");
            }
            catch (Exception ex)
            {
                LoadPort();
                await _dialogService.ErrorAsync("修改端口失败", ex.Message);
            }
        });
    }
}
