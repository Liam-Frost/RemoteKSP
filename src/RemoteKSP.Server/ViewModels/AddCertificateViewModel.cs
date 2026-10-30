using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RemoteKSP.Server.Services;

namespace RemoteKSP.Server.ViewModels;

/// <summary>Strings.Certs_AddTitle对话框。所有校验和落盘都在服务里做，这里只收集输入</summary>
public partial class AddCertificateViewModel : ViewModelBase
{
    private readonly PipeClient _pipeClient;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEToken), nameof(IsStore), nameof(IsPfx), nameof(IsTest))]
    private string _sourceType = "store";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isShared = true;

    // eToken
    [ObservableProperty] private string _cspProvider = "eToken Base Cryptographic Provider";
    [ObservableProperty] private string _keyContainer = "";
    [ObservableProperty] private string _etokenThumbprint = "";
    [ObservableProperty] private ObservableCollection<string> _containers = [];
    [ObservableProperty] private string _detectStatus = "";

    // 证书存储
    [ObservableProperty] private string _storeLocation = "LocalMachine";
    [ObservableProperty] private ObservableCollection<StoreCertOption> _storeCertificates = [];
    [ObservableProperty] private StoreCertOption? _selectedStoreCertificate;
    [ObservableProperty] private string _storeStatus = "";

    // PFX
    [ObservableProperty] private string _pfxPath = "";
    [ObservableProperty] private string _pfxStatus = "";

    /// <summary>由视图从 PasswordBox 同步（PasswordBox 不支持绑定）</summary>
    public string Pin { get; set; } = "";
    public string PfxPassword { get; set; } = "";

    /// <summary>生成测试 PFX 后通知视图把密码填进 PasswordBox</summary>
    public event Action<string>? PfxPasswordGenerated;

    public bool IsEToken { get => SourceType == "etoken"; set { if (value) SourceType = "etoken"; } }
    public bool IsStore { get => SourceType == "store"; set { if (value) SourceType = "store"; } }
    public bool IsPfx { get => SourceType == "pfx"; set { if (value) SourceType = "pfx"; } }
    public bool IsTest { get => SourceType == "test"; set { if (value) SourceType = "test"; } }

    public bool IsCurrentUserStore => StoreLocation == "CurrentUser";

    public AddCertificateViewModel(PipeClient pipeClient)
    {
        _pipeClient = pipeClient;
    }

    public Task InitializeAsync() => LoadStoreCertificatesAsync();

    partial void OnStoreLocationChanged(string value)
    {
        OnPropertyChanged(nameof(IsCurrentUserStore));
        _ = LoadStoreCertificatesAsync();
    }

    [RelayCommand]
    private async Task LoadStoreCertificatesAsync()
    {
        StoreStatus = Strings.Add_ReadingStore;
        StoreCertificates.Clear();
        try
        {
            var response = await _pipeClient.SendCommandAsync<StoreCertListResponse>(
                "ListStoreCertificates", new { StoreLocation });

            if (response == null)
            {
                StoreStatus = string.Format(Strings.Add_StoreEmpty, StoreLocation);
                return;
            }

            var certs = response.Certificates ?? [];
            foreach (var c in certs)
                StoreCertificates.Add(c);
            SelectedStoreCertificate = StoreCertificates.FirstOrDefault();

            if (certs.Length == 0)
                StoreStatus = response.DiagInfo ?? string.Format(Strings.Add_StoreEmpty, StoreLocation);
            else
                StoreStatus = "";
        }
        catch (Exception ex)
        {
            StoreStatus = ex.Message;
        }
    }

    record StoreCertListResponse(StoreCertOption[]? Certificates, string? DiagInfo);

    [RelayCommand]
    private async Task DetectContainersAsync()
    {
        DetectStatus = Strings.Add_Detecting;
        try
        {
            var containers = await _pipeClient.SendCommandAsync<string[]>(
                "DetectContainers", new { CspProvider }) ?? [];
            Containers = new ObservableCollection<string>(containers);
            if (string.IsNullOrEmpty(KeyContainer) && containers.Length > 0)
                KeyContainer = containers[0];
            DetectStatus = containers.Length == 0
                ? Strings.Add_NoEToken
                : string.Format(Strings.Add_ContainersFound, containers.Length);
        }
        catch (Exception ex)
        {
            DetectStatus = string.Format(Strings.Add_DetectFailed, ex.Message);
        }
    }

    [RelayCommand]
    private void BrowsePfx()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Add_PfxFilter,
            Title = Strings.Add_PfxPickTitle
        };
        if (dialog.ShowDialog() == true)
        {
            PfxPath = dialog.FileName;
            PfxStatus = Strings.Add_PfxCopied;
        }
    }

    [RelayCommand]
    private async Task GenerateTestPfxAsync()
    {
        try
        {
            var result = await _pipeClient.SendCommandAsync<GenerateTestCertResult>("GenerateTestCert", null)
                ?? throw new InvalidOperationException(Strings.Add_NoResult);
            PfxPath = result.PfxFile;
            PfxPassword = result.Password;
            PfxPasswordGenerated?.Invoke(result.Password);
            if (string.IsNullOrWhiteSpace(Name))
                Name = Strings.Cert_DefaultName_Pfx;
            PfxStatus = string.Format(Strings.Add_PfxGenerated, result.Thumbprint);
        }
        catch (Exception ex)
        {
            PfxStatus = string.Format(Strings.Add_GenerateFailed, ex.Message);
        }
    }

    /// <summary>提交给服务；成功返回 null，失败返回显示在对话框里的错误信息</summary>
    public async Task<string?> SubmitAsync()
    {
        object request;
        switch (SourceType)
        {
            case "etoken":
                if (string.IsNullOrWhiteSpace(KeyContainer) || string.IsNullOrWhiteSpace(EtokenThumbprint))
                    return Strings.Add_NeedContainerAndThumbprint;
                request = new { SourceType, Name, IsShared, CspProvider, KeyContainer,
                    Thumbprint = EtokenThumbprint.Trim(), Pin };
                break;
            case "store":
                if (SelectedStoreCertificate == null)
                    return Strings.Add_NeedCertificate;
                request = new { SourceType, Name, IsShared, StoreLocation,
                    SelectedStoreCertificate.Thumbprint };
                break;
            case "pfx":
                if (string.IsNullOrWhiteSpace(PfxPath))
                    return Strings.Add_NeedPfx;
                request = new { SourceType, Name, IsShared, PfxPath, PfxPassword };
                break;
            default:
                request = new { SourceType, Name = string.IsNullOrWhiteSpace(Name) ? Strings.Cert_DefaultName_Test : Name, IsShared };
                break;
        }

        await _pipeClient.SendCommandAsync<object>("AddCertificate", request);
        return null;
    }
}

public record GenerateTestCertResult(string PfxFile, string Password, string Thumbprint);
