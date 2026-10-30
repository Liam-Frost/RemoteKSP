using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RemoteKSP.Server.Services;

namespace RemoteKSP.Server.ViewModels;

/// <summary>"添加证书"对话框。所有校验和落盘都在服务里做，这里只收集输入</summary>
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
        StoreStatus = "正在读取证书存储...";
        StoreCertificates.Clear();
        try
        {
            var response = await _pipeClient.SendCommandAsync<StoreCertListResponse>(
                "ListStoreCertificates", new { StoreLocation });

            if (response == null)
            {
                StoreStatus = $"{StoreLocation}\\My 中没有带私钥的证书";
                return;
            }

            var certs = response.Certificates ?? [];
            foreach (var c in certs)
                StoreCertificates.Add(c);
            SelectedStoreCertificate = StoreCertificates.FirstOrDefault();

            if (certs.Length == 0)
                StoreStatus = response.DiagInfo ?? $"{StoreLocation}\\My 中没有带私钥的证书";
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
        DetectStatus = "正在检测...";
        try
        {
            var containers = await _pipeClient.SendCommandAsync<string[]>(
                "DetectContainers", new { CspProvider }) ?? [];
            Containers = new ObservableCollection<string>(containers);
            if (string.IsNullOrEmpty(KeyContainer) && containers.Length > 0)
                KeyContainer = containers[0];
            DetectStatus = containers.Length == 0
                ? "未检测到 eToken（未插入或驱动未安装）"
                : $"检测到 {containers.Length} 个密钥容器";
        }
        catch (Exception ex)
        {
            DetectStatus = $"检测失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void BrowsePfx()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PFX 文件|*.pfx;*.p12|所有文件|*.*",
            Title = "选择 PFX 文件"
        };
        if (dialog.ShowDialog() == true)
        {
            PfxPath = dialog.FileName;
            PfxStatus = "添加时服务会把文件复制到数据目录，之后移动或删除原文件不影响签名";
        }
    }

    [RelayCommand]
    private async Task GenerateTestPfxAsync()
    {
        try
        {
            var result = await _pipeClient.SendCommandAsync<GenerateTestCertResult>("GenerateTestCert", null)
                ?? throw new InvalidOperationException("服务未返回结果");
            PfxPath = result.PfxFile;
            PfxPassword = result.Password;
            PfxPasswordGenerated?.Invoke(result.Password);
            if (string.IsNullOrWhiteSpace(Name))
                Name = "测试 PFX 证书";
            PfxStatus = $"已生成自签名 PFX，指纹 {result.Thumbprint}，密码已自动填入";
        }
        catch (Exception ex)
        {
            PfxStatus = $"生成失败：{ex.Message}";
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
                    return "请填写密钥容器名和证书指纹";
                request = new { SourceType, Name, IsShared, CspProvider, KeyContainer,
                    Thumbprint = EtokenThumbprint.Trim(), Pin };
                break;
            case "store":
                if (SelectedStoreCertificate == null)
                    return "请选择一张证书";
                request = new { SourceType, Name, IsShared, StoreLocation,
                    SelectedStoreCertificate.Thumbprint };
                break;
            case "pfx":
                if (string.IsNullOrWhiteSpace(PfxPath))
                    return "请选择 PFX 文件";
                request = new { SourceType, Name, IsShared, PfxPath, PfxPassword };
                break;
            default:
                request = new { SourceType, Name = string.IsNullOrWhiteSpace(Name) ? "测试证书" : Name, IsShared };
                break;
        }

        await _pipeClient.SendCommandAsync<object>("AddCertificate", request);
        return null;
    }
}

public record GenerateTestCertResult(string PfxFile, string Password, string Thumbprint);
