using System;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteKSP.Client.ViewModels;

/// <summary>添加服务端表单的 ViewModel，带字段级验证</summary>
public partial class AddServerFormViewModel : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Add_EnterAddress))]
    [CustomValidation(typeof(AddServerFormViewModel), nameof(ValidateServerAddress))]
    private string _serverAddress = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Add_EnterPort))]
    [Range(1, 65535, ErrorMessageResourceType = typeof(Strings), ErrorMessageResourceName = nameof(Strings.Add_PortRange))]
    private string _port = "8443";

    [ObservableProperty]
    private string _name = "";

    public static ValidationResult? ValidateServerAddress(string? address, ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new ValidationResult(Strings.Add_EnterAddress);

        var trimmed = address.Trim();

        // Check if it's a valid IPv4, IPv6, or hostname
        if (Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
            return new ValidationResult(Strings.Add_InvalidAddress);

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
}
