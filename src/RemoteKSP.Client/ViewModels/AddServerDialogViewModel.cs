using System;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Client.Core;
using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace RemoteKSP.Client.ViewModels;

/// <summary>添加服务端对话框的 ViewModel</summary>
public partial class AddServerDialogViewModel : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "请输入服务器地址")]
    [CustomValidation(typeof(AddServerDialogViewModel), nameof(ValidateServerAddress))]
    private string _serverAddress = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "请输入端口")]
    [Range(1, 65535, ErrorMessage = "端口必须在 1 到 65535 之间")]
    private string _port = "8443";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isPairing;

    [ObservableProperty]
    private string _pairingStatus = "";

    [ObservableProperty]
    private bool _showVerificationCode;

    [ObservableProperty]
    private string _verificationCode = "";

    private PairingClient? _pairingClient;

    /// <summary>成功保存的配对，供对话框关闭后自动同步证书。</summary>
    public ServerEntry? PairedServer { get; private set; }

    public static ValidationResult? ValidateServerAddress(string? address, ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new ValidationResult("请输入服务器地址");

        var trimmed = address.Trim();

        // Check if it's a valid IPv4, IPv6, or hostname
        if (Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
            return new ValidationResult("服务器地址格式无效");

        return ValidationResult.Success;
    }

    public int PortNumber
    {
        get
        {
            if (int.TryParse(Port, out var port))
                return port;
            return 8443;
        }
    }

    partial void OnServerAddressChanged(string value)
    {
        ValidateProperty(value, nameof(ServerAddress));
    }

    partial void OnPortChanged(string value)
    {
        ValidateProperty(value, nameof(Port));
    }

    /// <summary>开始配对流程</summary>
    public async Task<bool> StartPairingAsync()
    {
        if (PairedServer != null)
            return true;
        if (IsPairing)
            return false;

        // 验证所有字段
        ValidateAllProperties();
        if (HasErrors)
            return false;

        IsPairing = true;
        ShowVerificationCode = false;

        try
        {
            var serverUrl = $"https://{ServerAddress.Trim()}:{PortNumber}";
            _pairingClient = new PairingClient();

            PairingStatus = "正在连接服务端...";
            var machineName = Environment.MachineName;
            var response = await _pairingClient.StartPairingAsync(serverUrl, machineName);

            // 计算验证码
            VerificationCode = RemoteKSP.Shared.Protocol.VerificationCode(
                _pairingClient.LastServerTlsThumbprint ?? throw new InvalidOperationException("未取得服务端 TLS 证书"),
                response.ClientNonce, response.ServerNonce);

            ShowVerificationCode = true;
            PairingStatus = "等待服务端批准...";

            // 轮询配对状态（最多等待 2 分钟）
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
                var result = await _pairingClient.CheckPairingStatusAsync(serverUrl, response.RequestId);

                if (result != null)
                {
                    // 配对成功，保存
                    var serverName = string.IsNullOrWhiteSpace(Name)
                        ? $"{ServerAddress}:{PortNumber}"
                        : Name.Trim();

                    PairedServer = ServerManager.RegisterServer(
                        serverUrl,
                        serverName,
                        result.ClientId,
                        result.Secret,
                        _pairingClient.LastServerTlsThumbprint
                    );
                    PairingStatus = "配对成功";
                    return true;
                }
            }

            PairingStatus = "配对超时";
            return false;
        }
        catch (Exception ex)
        {
            PairingStatus = $"配对失败: {ex.Message}";
            return false;
        }
        finally
        {
            IsPairing = false;
        }
    }
}
