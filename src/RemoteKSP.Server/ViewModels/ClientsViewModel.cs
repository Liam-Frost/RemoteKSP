using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Services;
using RemoteKSP.Server.Views.Dialogs;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Server.ViewModels;

public partial class ClientsViewModel : ViewModelBase
{
    private readonly PipeClient _pipeClient;
    private readonly DialogService _dialogService;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;

    [ObservableProperty]
    private ObservableCollection<ClientModel> _clients = [];

    [ObservableProperty]
    private ClientModel? _selectedClient;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasClients;

    [ObservableProperty]
    private string _emptyText = "暂无已配对客户端";

    public ClientsViewModel(PipeClient pipeClient, DialogService dialogService)
    {
        _pipeClient = pipeClient;
        _dialogService = dialogService;

        // 每5秒自动刷新客户端列表（静默刷新，保留选中项）
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += async (s, e) => await RefreshAsync(silent: true);
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        _refreshTimer.Start();
    }

    public void Cleanup()
    {
        _refreshTimer.Stop();
    }

    [RelayCommand]
    private async Task RefreshAsync(bool silent = false)
    {
        if (!silent)
            IsLoading = true;

        var selectedId = SelectedClient?.Id;

        try
        {
            var dtos = await _pipeClient.SendCommandAsync<ClientDto[]>("GetClients", null);

            Clients.Clear();
            if (dtos != null)
            {
                foreach (var dto in dtos)
                {
                    Clients.Add(new ClientModel
                    {
                        Id = dto.Id,
                        MachineName = dto.MachineName,
                        IpAddress = dto.IpAddress,
                        PairedAt = dto.PairedAt.ToLocalTime(),
                        LastSeen = dto.LastSeen.ToLocalTime(),
                        AllowedCertIds = dto.AllowedCertIds
                    });
                }
            }
            HasClients = Clients.Any();
            EmptyText = "暂无已配对客户端";
        }
        catch
        {
            Clients.Clear();
            HasClients = false;
            EmptyText = "服务未运行，无法加载客户端列表";
        }
        finally
        {
            IsLoading = false;
        }
    }
    [RelayCommand(CanExecute = nameof(CanRevokeClient))]
    private async Task RevokeAsync()
    {
        if (SelectedClient == null) return;

        var client = SelectedClient; // Capture before dialog
        var confirmed = await _dialogService.ConfirmAsync(
            "撤销客户端授权",
            $"确定要撤销客户端的授权吗？\n\n撤销后，该客户端将无法继续使用远程签名功能。",
            "撤销",
            destructive: true,
            new DialogField("机器名", client.MachineName, Monospace: false),
            new DialogField("客户端 ID", client.Id),
            new DialogField("IP 地址", client.IpAddress, Monospace: false));

        if (!confirmed) return;

        try
        {
            await _pipeClient.SendCommandAsync<object>("RevokeClient", new { ClientId = client.Id });
            await _dialogService.SuccessAsync("撤销成功", $"已撤销客户端 {client.MachineName} 的授权");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("撤销失败", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevokeClient))]
    private async Task ManagePermissionsAsync()
    {
        if (SelectedClient == null) return;
        var client = SelectedClient;

        CertificateStatus[] certs;
        try
        {
            certs = await _pipeClient.SendCommandAsync<CertificateStatus[]>("GetCertificates") ?? [];
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("加载证书失败", ex.Message);
            return;
        }

        var form = new ClientPermissionsForm(
            $"选择客户端 {client.MachineName} 可以使用的证书。客户端下次更新证书时，未授权的证书会自动解绑。",
            certs.Select(c => new CertificateItem(c)), client.AllowedCertIds);

        var saved = await _dialogService.ShowFormAsync("管理证书权限", form, "保存", async () =>
        {
            await _pipeClient.SendCommandAsync<object>("SetClientPermissions",
                new { ClientId = client.Id, AllowedCertIds = form.Result });
            return null;
        });
        if (saved)
            await RefreshAsync();
    }

    private bool CanRevokeClient() => SelectedClient != null;

    partial void OnSelectedClientChanged(ClientModel? value)
    {
        RevokeCommand.NotifyCanExecuteChanged();
        ManagePermissionsCommand.NotifyCanExecuteChanged();
    }
}

public class ClientModel
{
    public string Id { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime PairedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public string PairedAtStr => PairedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string LastSeenStr => LastSeen.ToString("yyyy-MM-dd HH:mm:ss");
    public List<string>? AllowedCertIds { get; set; }
    public string PermissionText => AllowedCertIds switch
    {
        null => "全部分享证书",
        { Count: 0 } => "全部禁止",
        var ids => $"{ids.Count} 个证书"
    };
}
