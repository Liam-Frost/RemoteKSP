using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteKSP.Server.ViewModels;

/// <summary>配对审批对话框 ViewModel</summary>
public partial class PairingApprovalDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _machineName = "";
    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _ipAddress = "";
    [ObservableProperty] private string _verificationCode = "";
    [ObservableProperty] private DateTime _createdAt;
    [ObservableProperty] private DateTime _expiresAt;
    [ObservableProperty] private string _requestId = "";

    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private bool _isExpired;

    public string CreatedAtStr => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string ExpiresAtStr => ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public TimeSpan RemainingTime => ExpiresAt - DateTime.UtcNow;
    public string RemainingTimeStr
    {
        get
        {
            var remaining = RemainingTime;
            if (remaining.TotalSeconds < 0)
                return Strings.State_Expired;

            if (remaining.TotalMinutes >= 1)
                return string.Format(Strings.Pairing_Minutes, (int)remaining.TotalMinutes);

            return string.Format(Strings.Pairing_Seconds, (int)remaining.TotalSeconds);
        }
    }

    public bool CanApprove => !IsProcessing && !IsExpired;

    public void CheckExpiration()
    {
        IsExpired = DateTime.UtcNow >= ExpiresAt;
        OnPropertyChanged(nameof(RemainingTimeStr));
        OnPropertyChanged(nameof(CanApprove));
    }
}
