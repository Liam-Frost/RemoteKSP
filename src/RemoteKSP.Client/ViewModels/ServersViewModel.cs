using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Client.Core;
using RemoteKSP.Client.Views.Dialogs;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Client.ViewModels;

/// <summary>服务端管理页：已配对服务端列表 + 添加（配对）新服务端</summary>
public partial class ServersViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly Wpf.Ui.IContentDialogService _contentDialogService;
    private readonly System.Windows.Threading.DispatcherTimer _statusCheckTimer;

    [ObservableProperty] private ObservableCollection<ServerItem> _servers = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCertificatesCommand), nameof(RenameCommand), nameof(RemoveCommand))]
    private ServerItem? _selectedServer;

    [ObservableProperty] private bool _hasServers;
    [ObservableProperty] private bool _isBusy;

    // 添加服务端
    [ObservableProperty] private string _newServerName = "";
    [ObservableProperty] private string _serverIp = "";
    [ObservableProperty] private string _serverPort = "8443";
    [ObservableProperty] private string _verificationCode = "";
    [ObservableProperty] private bool _showVerificationCode;
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private string _pairingStatus = "";

    public ServersViewModel(NotificationService notificationService, Wpf.Ui.IContentDialogService contentDialogService)
    {
        _notificationService = notificationService;
        _contentDialogService = contentDialogService;

        // 状态检查定时器（每30秒检查一次）
        _statusCheckTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _statusCheckTimer.Tick += (s, e) => CheckAllServersStatusAsync();
    }

    public async Task InitializeAsync()
    {
        // RefreshAsync 末尾会立即检查一次状态
        await RefreshAsync();
        _statusCheckTimer.Start();
    }

    public void Cleanup()
    {
        _statusCheckTimer.Stop();
    }

    private void CheckAllServersStatusAsync()
    {
        foreach (var server in Servers)
        {
            _ = CheckServerStatusAsync(server);
        }
    }

    private async Task CheckServerStatusAsync(ServerItem server)
    {
        try
        {
            var status = await ServerStatusChecker.CheckStatusAsync(server.Entry);

            // 确保在 UI 线程上更新属性
            System.Windows.Application.Current.Dispatcher.Invoke(() => ApplyStatus(server, status));
        }
        catch
        {
            // 静默失败，保持当前状态
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var selectedId = SelectedServer?.Entry.Id;
        // 保留上一次检测到的状态，避免刷新时闪回「未知」
        var previous = Servers.ToDictionary(s => s.Entry.Id, s => s.Status);
        var items = await Task.Run(() =>
        {
            var bindings = ClientRegistry.LoadCertificates();
            return ClientRegistry.LoadServers()
                .Select(s => new ServerItem(s, bindings.Count(b => b.ServerId == s.Id)))
                .ToList();
        });
        foreach (var item in items)
            if (previous.TryGetValue(item.Entry.Id, out var status))
                ApplyStatus(item, status);

        Servers = new ObservableCollection<ServerItem>(items);
        SelectedServer = Servers.FirstOrDefault(s => s.Entry.Id == selectedId);
        HasServers = Servers.Any();

        CheckAllServersStatusAsync();
    }

    private static void ApplyStatus(ServerItem server, ServerStatus status)
    {
        server.Status = status;
        server.StatusText = ServerStatusChecker.GetStatusText(status);
        server.StatusIcon = ServerStatusChecker.GetStatusIcon(status);
        server.StatusKind = ServerStatusChecker.GetStatusKind(status);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SyncCertificatesAsync()
    {
        IsBusy = true;
        try
        {
            await SyncUi.SyncAndReportAsync(SelectedServer!.Entry, _notificationService);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        var server = SelectedServer!.Entry;
        var form = new TextInputForm(Strings.Srv_Name, server.ServerName, Strings.Srv_NameHint);
        var saved = await _notificationService.ShowFormAsync(Strings.Srv_RenameTitle, form, Strings.Common_Save, () =>
        {
            if (string.IsNullOrWhiteSpace(form.Value))
                return Task.FromResult<string?>(Strings.Srv_NameRequired);
            ClientRegistry.RenameServer(server.Id, form.Value.Trim());
            return Task.FromResult<string?>(null);
        });
        if (saved)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        var item = SelectedServer!;
        var confirmed = await _notificationService.ConfirmAsync(
            Strings.Srv_RemoveTitle,
            string.Format(Strings.Srv_RemoveMessage, item.Name, item.CertificateCount),
            Strings.Srv_Remove,
            destructive: true,
            new DialogField(Strings.Srv_Address, item.Url),
            new DialogField(Strings.Common_ClientId, item.ClientId));
        if (!confirmed)
            return;

        try
        {
            IsBusy = true;
            var (unbound, notified) = await ServerManager.RemoveServerAsync(item.Entry);
            await RefreshAsync();

            // 服务端已收到通知时列表刷新即可；没通知到要提醒用户，服务端那边仍保留着授权
            if (!notified)
                await _notificationService.ShowWarningAsync(Strings.Srv_RemovedLocallyTitle,
                    string.Format(Strings.Srv_RemovedLocally, item.Name, unbound));
        }
        catch (Exception ex)
        {
            await _notificationService.ShowErrorAsync(Strings.Srv_RemoveFailed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasSelection() => SelectedServer != null && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SyncCertificatesCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ShowAddServerDialogCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddServer() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAddServer))]
    private async Task ShowAddServerDialogAsync()
    {
        var vm = new AddServerDialogViewModel();
        var form = new AddServerForm { DataContext = vm };

        var dialog = new Wpf.Ui.Controls.ContentDialog
        {
            Title = Strings.Srv_AddTitle,
            Content = form,
            PrimaryButtonText = Strings.Srv_StartPairing,
            CloseButtonText = Strings.Common_Cancel,
            DefaultButton = Wpf.Ui.Controls.ContentDialogButton.Primary,
            DialogMaxWidth = 600
        };

        bool pairingSucceeded = false;
        dialog.Closing += async (_, e) =>
        {
            if (e.Result != Wpf.Ui.Controls.ContentDialogResult.Primary || pairingSucceeded)
                return;

            // Keep the dialog open while pairing
            e.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;

            pairingSucceeded = await vm.StartPairingAsync();

            if (pairingSucceeded)
            {
                await Task.Delay(500); // 短暂显示成功状态
                dialog.Hide(Wpf.Ui.Controls.ContentDialogResult.Primary);
            }
            else
            {
                dialog.IsPrimaryButtonEnabled = true;
            }
        };

        await _contentDialogService.ShowAsync(dialog, default);

        // 先关闭配对对话框，再显示同步结果或证书冲突确认，避免嵌套模态窗口。
        if (pairingSucceeded && vm.PairedServer is { } server)
        {
            IsBusy = true;
            try
            {
                await RefreshAsync();
                SelectedServer = Servers.FirstOrDefault(s => s.Entry.Id == server.Id);
                await SyncUi.SyncAndReportAsync(server, _notificationService);
            }
            finally
            {
                try
                {
                    await RefreshAsync();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }
    }

    [RelayCommand]
    private async Task StartPairingAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerIp) || string.IsNullOrWhiteSpace(ServerPort))
        {
            await _notificationService.ShowWarningAsync(Strings.Srv_MissingInfoTitle, Strings.Srv_MissingInfo);
            return;
        }

        var serverUrl = $"https://{ServerIp.Trim()}:{ServerPort.Trim()}";
        var name = string.IsNullOrWhiteSpace(NewServerName) ? ServerIp.Trim() : NewServerName.Trim();

        IsPairing = true;
        ShowVerificationCode = false;
        PairingStatus = Strings.Pair_Connecting;

        try
        {
            var client = new PairingClient();
            var response = await client.StartPairingAsync(serverUrl, Environment.MachineName);

            PairingStatus = Strings.Pair_WaitingAdmin;
            if (!string.IsNullOrEmpty(client.LastServerTlsThumbprint))
            {
                VerificationCode = Shared.Protocol.VerificationCode(
                    client.LastServerTlsThumbprint, response.ClientNonce, response.ServerNonce);
                ShowVerificationCode = true;
            }

            PairingResult? result = null;
            for (int i = 0; i < 60 && result == null; i++)
            {
                await Task.Delay(2000);
                result = await client.CheckPairingStatusAsync(serverUrl, response.RequestId);
            }

            if (result == null)
            {
                IsPairing = false;
                await _notificationService.ShowErrorAsync(Strings.Pair_TimedOut, Strings.Pair_TimedOutMessage);
                return;
            }

            var server = ServerManager.RegisterServer(serverUrl, name, result.ClientId, result.Secret,
                client.LastServerTlsThumbprint);

            PairingStatus = Strings.Pair_SucceededFetching;
            ShowVerificationCode = false;
            IsPairing = false;
            NewServerName = "";
            ServerIp = "";
            await RefreshAsync();
            SelectedServer = Servers.FirstOrDefault(s => s.Entry.Id == server.Id);

            await SyncUi.SyncAndReportAsync(server, _notificationService);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            IsPairing = false;
            await _notificationService.ShowErrorAsync(Strings.Pair_FailedTitle, Strings.Pair_Failed,
                new DialogField(Strings.Common_ErrorMessage, ex.Message, Monospace: false));
        }
        finally
        {
            IsPairing = false;
            ShowVerificationCode = false;
        }
    }
}

/// <summary>服务端列表的一行</summary>
public partial class ServerItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public ServerEntry Entry { get; }
    public int CertificateCount { get; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private ServerStatus _status = ServerStatus.Unknown;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _statusText = Strings.Status_Unknown;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _statusIcon = "Question24";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _statusKind = "Neutral";

    public ServerItem(ServerEntry entry, int certificateCount)
    {
        Entry = entry;
        CertificateCount = certificateCount;
    }

    public string Name => Entry.ServerName;
    public string Url => Entry.ServerUrl;
    public string ClientId => Entry.ClientId;
    public string CertificateCountText => string.Format(Strings.Srv_CertCount, CertificateCount);
    public string TlsText => Entry.TlsThumbprint ?? Strings.Srv_TlsNotRecorded;
}
