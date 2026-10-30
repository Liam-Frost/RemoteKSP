using System;
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

/// <summary>证书管理页：服务端分享给客户端的所有签名证书</summary>
public partial class CertificatesViewModel : ViewModelBase
{
    private readonly PipeClient _pipeClient;
    private readonly DialogService _dialogService;

    [ObservableProperty] private ObservableCollection<CertificateItem> _certificates = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(RemoveCommand), nameof(SetPinCommand),
        nameof(SetDefaultCommand))]
    private CertificateItem? _selectedCertificate;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasCertificates;
    [ObservableProperty] private bool _serviceAvailable = true;
    [ObservableProperty] private string _emptyText = Strings.Certs_Empty;

    public CertificatesViewModel(PipeClient pipeClient, DialogService dialogService)
    {
        _pipeClient = pipeClient;
        _dialogService = dialogService;
    }

    public Task InitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        var selectedId = SelectedCertificate?.Id;
        try
        {
            var statuses = await _pipeClient.SendCommandAsync<CertificateStatus[]>("GetCertificates") ?? [];
            Certificates = new ObservableCollection<CertificateItem>(statuses.Select(s => new CertificateItem(s)));
            SelectedCertificate = Certificates.FirstOrDefault(c => c.Id == selectedId);
            ServiceAvailable = true;
            EmptyText = Strings.Certs_Empty;
        }
        catch
        {
            Certificates.Clear();
            ServiceAvailable = false;
            EmptyText = Strings.Certs_ServiceNotRunning;
        }
        finally
        {
            HasCertificates = Certificates.Any();
            IsLoading = false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadKeySourcesCommand))]
    private bool _isReloading;

    /// <summary>
    /// 让服务重新加载所有证书的密钥源（例如插好 eToken 后），同时重置自动重试次数。
    /// 已就绪的证书也会重新加载；eToken 枚举容器可能需要几秒。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadKeySourcesAsync()
    {
        IsReloading = true;
        try
        {
            await _pipeClient.SendCommandAsync<KeySourceStatus>("ReloadKeySource");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Certs_ReloadFailedTitle, Strings.Certs_ReloadFailed,
                new DialogField(Strings.Common_ErrorMessage, ex.Message, Monospace: false));
            return;
        }
        finally
        {
            IsReloading = false;
        }

        // 缺 PIN 和测试证书本来就不是「就绪」，重新加载解决不了，不算失败
        var failed = Certificates
            .Where(c => !c.IsReady && !c.Status.NeedsPin && c.Status.SourceType != "test")
            .ToList();
        if (failed.Count == 0)
            await _dialogService.SuccessAsync(Strings.Certs_ReloadedTitle, string.Format(Strings.Certs_Reloaded, Certificates.Count));
        else
            await _dialogService.WarningAsync(Strings.Certs_PartlyUnavailableTitle,
                string.Format(Strings.Certs_PartlyUnavailable, Certificates.Count, failed.Count),
                failed.Select(c => new DialogField(c.Name, c.Detail, Monospace: false)).ToArray());
    }

    private bool CanReload() => !IsReloading;

    [RelayCommand]
    private async Task AddAsync()
    {
        var form = new AddCertificateForm(new AddCertificateViewModel(_pipeClient));
        if (await _dialogService.ShowFormAsync(Strings.Certs_AddTitle, form, Strings.Common_Add, form.ViewModel.SubmitAsync, maxWidth: 640))
            await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        var cert = SelectedCertificate!;
        var form = new EditCertificateForm(cert.Name, cert.IsShared);
        var saved = await _dialogService.ShowFormAsync(Strings.Certs_EditTitle, form, Strings.Common_Save, async () =>
        {
            if (string.IsNullOrWhiteSpace(form.Model.Name))
                return Strings.Certs_NameRequired;
            await _pipeClient.SendCommandAsync<object>("UpdateCertificate",
                new { cert.Id, form.Model.Name, form.Model.IsShared });
            return null;
        });
        if (saved)
            await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSetPin))]
    private async Task SetPinAsync()
    {
        var cert = SelectedCertificate!;
        var message = string.Format(Strings.Certs_PinPrompt, cert.Name, PinAttemptLimiter.MaxFailures,
            PinAttemptLimiter.LockoutDuration.TotalMinutes.ToString("0"));
        if (cert.IsDisabled)
            message = $"{cert.Detail}\n\n{message}";
        var form = new PinForm(message);
        var saved = await _dialogService.ShowFormAsync(Strings.Certs_VerifyPinTitle, form, Strings.Certs_VerifyAndSave, async () =>
        {
            if (string.IsNullOrEmpty(form.Pin))
                return Strings.Certs_EnterPin;
            await _pipeClient.SendCommandAsync<object>("SetCertificatePin", new { cert.Id, form.Pin });
            return null;
        });
        if (saved)
        {
            await RefreshAsync();
            await _dialogService.SuccessAsync(Strings.Certs_PinSavedTitle, Strings.Certs_PinSaved);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSetDefault))]
    private async Task SetDefaultAsync()
    {
        try
        {
            await _pipeClient.SendCommandAsync<object>("SetDefaultCertificate", new { SelectedCertificate!.Id });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_SetFailed, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        var cert = SelectedCertificate!;
        var confirmed = await _dialogService.ConfirmAsync(
            Strings.Certs_DeleteTitle,
            Strings.Certs_DeleteMessage,
            Strings.Common_Delete,
            destructive: true,
            new DialogField(Strings.Common_Name, cert.Name, Monospace: false),
            new DialogField(Strings.Common_Thumbprint, cert.Thumbprint));
        if (!confirmed)
            return;

        try
        {
            await _pipeClient.SendCommandAsync<object>("RemoveCertificate", new { cert.Id });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync(Strings.Common_DeleteFailed, ex.Message);
        }
    }

    private bool HasSelection() => SelectedCertificate != null;
    private bool CanSetPin() => SelectedCertificate?.IsEToken == true;
    private bool CanSetDefault() => SelectedCertificate is { IsShared: true, IsDefault: false };
}
