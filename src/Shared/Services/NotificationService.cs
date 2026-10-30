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
            details = "证书已是最新。";
        }
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (added > 0) parts.Add($"新增 {added} 张");
            if (updated > 0) parts.Add($"更新 {updated} 张");
            if (removed > 0) parts.Add($"解绑 {removed} 张");
            details = string.Join(" · ", parts);
        }

        return _dialogService.SuccessAsync("证书同步完成", $"已成功同步服务端「{serverName}」的证书。\n\n{details}");
    }

    /// <summary>Show multi-server sync summary</summary>
    public Task ShowMultiServerSyncSuccessAsync(int serverCount, int totalAdded, int totalUpdated, int totalRemoved)
    {
        string details;
        if (totalAdded == 0 && totalUpdated == 0 && totalRemoved == 0)
        {
            details = "所有证书均已是最新。";
        }
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (totalAdded > 0) parts.Add($"新增 {totalAdded} 张");
            if (totalUpdated > 0) parts.Add($"更新 {totalUpdated} 张");
            if (totalRemoved > 0) parts.Add($"解绑 {totalRemoved} 张");
            details = string.Join(" · ", parts);
        }

        return _dialogService.SuccessAsync("证书同步完成", $"已成功同步 {serverCount} 台服务端的证书。\n\n{details}");
    }

    /// <summary>Copy to clipboard and show success dialog</summary>
    public Task CopyToClipboardAsync(string text, string itemName)
    {
        Clipboard.SetText(text);
        return _dialogService.SuccessAsync("复制成功", $"{itemName}已复制到剪贴板。");
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
