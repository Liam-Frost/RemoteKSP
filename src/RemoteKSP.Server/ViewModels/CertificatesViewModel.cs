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
    [ObservableProperty] private string _emptyText = "暂无证书";

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
            EmptyText = "暂无证书";
        }
        catch
        {
            Certificates.Clear();
            ServiceAvailable = false;
            EmptyText = "服务未运行，无法加载证书列表";
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
            await _dialogService.ErrorAsync("重新加载失败", "无法让服务重新加载密钥源。",
                new DialogField("错误信息", ex.Message, Monospace: false));
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
            await _dialogService.SuccessAsync("重新加载完成", $"已重新加载 {Certificates.Count} 个证书的密钥源。");
        else
            await _dialogService.WarningAsync("部分证书仍不可用",
                $"已重新加载 {Certificates.Count} 个证书，{failed.Count} 个仍不可用。",
                failed.Select(c => new DialogField(c.Name, c.Detail, Monospace: false)).ToArray());
    }

    private bool CanReload() => !IsReloading;

    [RelayCommand]
    private async Task AddAsync()
    {
        var form = new AddCertificateForm(new AddCertificateViewModel(_pipeClient));
        if (await _dialogService.ShowFormAsync("添加证书", form, "添加", form.ViewModel.SubmitAsync, maxWidth: 640))
            await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        var cert = SelectedCertificate!;
        var form = new EditCertificateForm(cert.Name, cert.IsShared);
        var saved = await _dialogService.ShowFormAsync("编辑证书", form, "保存", async () =>
        {
            if (string.IsNullOrWhiteSpace(form.Model.Name))
                return "名称不能为空";
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
        var message = $"输入证书「{cert.Name}」所在 eToken 的 PIN。连续失败 {PinAttemptLimiter.MaxFailures} 次后需等待 " +
                      $"{PinAttemptLimiter.LockoutDuration.TotalMinutes:0} 分钟才能再试。";
        if (cert.IsDisabled)
            message = $"{cert.Detail}\n\n{message}";
        var form = new PinForm(message);
        var saved = await _dialogService.ShowFormAsync("验证 PIN", form, "验证并保存", async () =>
        {
            if (string.IsNullOrEmpty(form.Pin))
                return "请输入 PIN";
            await _pipeClient.SendCommandAsync<object>("SetCertificatePin", new { cert.Id, form.Pin });
            return null;
        });
        if (saved)
        {
            await RefreshAsync();
            await _dialogService.SuccessAsync("PIN 已保存", "测试签名成功，PIN 已加密保存，签名服务立即生效。");
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
            await _dialogService.ErrorAsync("设置失败", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        var cert = SelectedCertificate!;
        var confirmed = await _dialogService.ConfirmAsync(
            "删除证书",
            "确定要从服务端删除这张证书吗？\n\n删除后客户端将无法再用它签名，下次更新证书时会自动解绑。证书本身（eToken、证书存储中的原件）不会被删除。",
            "删除",
            destructive: true,
            new DialogField("名称", cert.Name, Monospace: false),
            new DialogField("指纹", cert.Thumbprint));
        if (!confirmed)
            return;

        try
        {
            await _pipeClient.SendCommandAsync<object>("RemoveCertificate", new { cert.Id });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("删除失败", ex.Message);
        }
    }

    private bool HasSelection() => SelectedCertificate != null;
    private bool CanSetPin() => SelectedCertificate?.IsEToken == true;
    private bool CanSetDefault() => SelectedCertificate is { IsShared: true, IsDefault: false };
}
