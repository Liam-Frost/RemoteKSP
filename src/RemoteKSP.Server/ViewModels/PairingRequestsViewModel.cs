using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Server.Services;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Server.ViewModels;

public partial class PairingRequestsViewModel : ViewModelBase
{
    private readonly PipeClient _pipeClient;
    private readonly DialogService _dialogService;

    [ObservableProperty]
    private ObservableCollection<PairingRequestModel> _requests = [];

    [ObservableProperty]
    private PairingRequestModel? _selectedRequest;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasRequests;

    [ObservableProperty]
    private string _emptyText = Strings.Pairing_Empty;

    public PairingRequestsViewModel(PipeClient pipeClient, DialogService dialogService)
    {
        _pipeClient = pipeClient;
        _dialogService = dialogService;
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var dtos = await _pipeClient.SendCommandAsync<PairingRequestDto[]>("GetPendingRequests", null);

            Requests.Clear();
            if (dtos != null)
            {
                foreach (var dto in dtos)
                {
                    Requests.Add(new PairingRequestModel
                    {
                        RequestId = dto.RequestId,
                        MachineName = dto.MachineName,
                        IpAddress = dto.IpAddress,
                        VerificationCode = dto.VerificationCode,
                        CreatedAt = dto.CreatedAt.ToLocalTime(),
                        ExpiresAt = dto.ExpiresAt.ToLocalTime()
                    });
                }
            }
            HasRequests = Requests.Any();
            EmptyText = Strings.Pairing_Empty;
        }
        catch
        {
            Requests.Clear();
            HasRequests = false;
            EmptyText = Strings.Pairing_ServiceNotRunning;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApproveOrReject))]
    private async Task ApproveAsync()
    {
        if (SelectedRequest == null) return;

        try
        {
            await _pipeClient.SendCommandAsync<object>("ApprovePairing", new { SelectedRequest.RequestId });
            await _dialogService.SuccessAsync(Strings.Pairing_ApprovedTitle,
                string.Format(Strings.Pairing_ApprovedFrom, SelectedRequest.MachineName, SelectedRequest.IpAddress));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Pairing_ApproveFailed, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApproveOrReject))]
    private async Task RejectAsync()
    {
        if (SelectedRequest == null) return;

        try
        {
            await _pipeClient.SendCommandAsync<object>("RejectPairing", new { SelectedRequest.RequestId });
            await _dialogService.InfoAsync(Strings.Pairing_RejectedTitle,
                string.Format(Strings.Pairing_RejectedFrom, SelectedRequest.MachineName, SelectedRequest.IpAddress));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Pairing_RejectFailed, ex.Message);
        }
    }

    private bool CanApproveOrReject() => SelectedRequest != null;

    partial void OnSelectedRequestChanged(PairingRequestModel? value)
    {
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }
}

public class PairingRequestModel
{
    public string RequestId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string VerificationCode { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string CreatedAtStr => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string ExpiresAtStr => ExpiresAt.ToString("yyyy-MM-dd HH:mm:ss");
}

public record PairingRequestDto(string RequestId, string MachineName, string IpAddress,
    string VerificationCode, DateTime CreatedAt, DateTime ExpiresAt);
