using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace RemoteKSP.Shared.Models;

public enum OperationState
{
    Idle,
    Running,
    Success,
    Warning,
    Error
}

public partial class OperationStatus : ObservableObject
{
    [ObservableProperty]
    private OperationState _state = OperationState.Idle;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _message = "";

    public bool IsOpen => State != OperationState.Idle;

    public InfoBarSeverity Severity => State switch
    {
        OperationState.Success => InfoBarSeverity.Success,
        OperationState.Warning => InfoBarSeverity.Warning,
        OperationState.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    partial void OnStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Severity));
    }

    public void Run(string? title = null)
    {
        State = OperationState.Running;
        Title = title ?? SharedStrings.Status_Processing;
        Message = "";
    }

    public void Success(string message)
    {
        State = OperationState.Success;
        Title = SharedStrings.Status_Success;
        Message = message;
    }

    public void Warn(string message)
    {
        State = OperationState.Warning;
        Title = SharedStrings.Status_Warning;
        Message = message;
    }

    public void Error(string message)
    {
        State = OperationState.Error;
        Title = SharedStrings.Status_Error;
        Message = message;
    }

    public void Clear()
    {
        State = OperationState.Idle;
        Title = "";
        Message = "";
    }
}
