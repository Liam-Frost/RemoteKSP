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
    private string _emptyText = Strings.Clients_Empty;

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
            EmptyText = Strings.Clients_Empty;
        }
        catch
        {
            Clients.Clear();
            HasClients = false;
            EmptyText = Strings.Clients_ServiceNotRunning;
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
            Strings.Clients_RevokeTitle,
            Strings.Clients_RevokeMessage,
            Strings.Clients_Revoke,
            destructive: true,
            new DialogField(Strings.Common_MachineName, client.MachineName, Monospace: false),
            new DialogField(Strings.Common_ClientId, client.Id),
            new DialogField(Strings.Common_IpAddress, client.IpAddress, Monospace: false));

        if (!confirmed) return;

        try
        {
            await _pipeClient.SendCommandAsync<object>("RevokeClient", new { ClientId = client.Id });
            await _dialogService.SuccessAsync(Strings.Clients_RevokedTitle, string.Format(Strings.Clients_Revoked, client.MachineName));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Clients_RevokeFailed, ex.Message);
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
            await _dialogService.ErrorAsync(Strings.Clients_LoadCertsFailed, ex.Message);
            return;
        }

        var form = new ClientPermissionsForm(
            string.Format(Strings.Clients_PermissionsPrompt, client.MachineName),
            certs.Select(c => new CertificateItem(c)), client.AllowedCertIds);

        var saved = await _dialogService.ShowFormAsync(Strings.Clients_PermissionsTitle, form, Strings.Common_Save, async () =>
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
        null => Strings.Clients_AllShared,
        { Count: 0 } => Strings.Clients_NoneAllowed,
        var ids => string.Format(Strings.Clients_CertCount, ids.Count)
    };
}
