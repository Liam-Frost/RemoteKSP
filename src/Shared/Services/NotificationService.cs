using System;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RemoteKSP.Shared.Services;

/// <summary>
/// Unified notification service for user feedback.
/// Uses DialogService for all success/error feedback to maintain consistency with Server.
/// </summary>
public class NotificationService
{
    private readonly DialogService _dialogService;

    public NotificationService(ISnackbarService snackbarService, DialogService dialogService)
    {
        _dialogService = dialogService;
        // snackbarService 保留但不再用于成功反馈
    }

    /// <summary>Show certificate sync success dialog</summary>
    public Task ShowSyncSuccessAsync(string serverName, int added, int updated, int removed)
    {
        string details;
        if (added == 0 && updated == 0 && removed == 0)
        {
            details = SharedStrings.Sync_UpToDate;
        }
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (added > 0) parts.Add(string.Format(SharedStrings.Sync_Added, added));
            if (updated > 0) parts.Add(string.Format(SharedStrings.Sync_Updated, updated));
            if (removed > 0) parts.Add(string.Format(SharedStrings.Sync_Unbound, removed));
            details = string.Join(" · ", parts);
        }

        return _dialogService.SuccessAsync(SharedStrings.Sync_SuccessTitle, string.Format(SharedStrings.Sync_ServerSynced, serverName, details));
    }

    /// <summary>Show multi-server sync summary</summary>
    public Task ShowMultiServerSyncSuccessAsync(int serverCount, int totalAdded, int totalUpdated, int totalRemoved)
    {
        string details;
        if (totalAdded == 0 && totalUpdated == 0 && totalRemoved == 0)
        {
            details = SharedStrings.Sync_AllUpToDate;
        }
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (totalAdded > 0) parts.Add(string.Format(SharedStrings.Sync_Added, totalAdded));
            if (totalUpdated > 0) parts.Add(string.Format(SharedStrings.Sync_Updated, totalUpdated));
            if (totalRemoved > 0) parts.Add(string.Format(SharedStrings.Sync_Unbound, totalRemoved));
            details = string.Join(" · ", parts);
        }

        return _dialogService.SuccessAsync(SharedStrings.Sync_SuccessTitle, string.Format(SharedStrings.Sync_ServersSynced, serverCount, details));
    }

    /// <summary>Copy to clipboard and show success dialog</summary>
    public Task CopyToClipboardAsync(string text, string itemName)
    {
        Clipboard.SetText(text);
        return _dialogService.SuccessAsync(SharedStrings.Copy_SuccessTitle, string.Format(SharedStrings.Copy_SuccessMessage, itemName));
    }

    /// <summary>Show blocking error dialog (for complex errors with technical details)</summary>
    public Task ShowErrorAsync(string title, string message, params DialogField[] fields) =>
        _dialogService.ErrorAsync(title, message, fields);

    /// <summary>Show blocking warning dialog</summary>
    public Task ShowWarningAsync(string title, string message, params DialogField[] fields) =>
        _dialogService.WarningAsync(title, message, fields);

    /// <summary>Show blocking info dialog (when user needs to acknowledge something important)</summary>
    public Task ShowInfoDialogAsync(string title, string message, params DialogField[] fields) =>
        _dialogService.InfoAsync(title, message, fields);

    /// <summary>Show confirmation dialog</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText,
        bool destructive = false, params DialogField[] fields) =>
        _dialogService.ConfirmAsync(title, message, confirmText, destructive, fields);

    /// <summary>Show form dialog (for user input with validation)</summary>
    public Task<bool> ShowFormAsync(string title, System.Windows.FrameworkElement form, string primaryText,
        Func<Task<string?>> submit, double maxWidth = 560) =>
        _dialogService.ShowFormAsync(title, form, primaryText, submit, maxWidth);
}
