using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RemoteKSP.Shared.Services;

public enum DialogKind { Info, Success, Warning, Error }

/// <summary>A technical value shown in a dialog as a selectable, copyable field.</summary>
public record DialogField(string Label, string Value, bool Monospace = true);

/// <summary>
/// App-wide Fluent dialogs (WPF-UI ContentDialog) replacing MessageBox.
/// Requires MainWindow to register its ContentDialogHost with IContentDialogService.
/// </summary>
public class DialogService
{
    private readonly IContentDialogService _contentDialogService;

    public DialogService(IContentDialogService contentDialogService)
    {
        _contentDialogService = contentDialogService;
    }

    public Task InfoAsync(string title, string message, params DialogField[] fields) =>
        ShowAsync(DialogKind.Info, title, message, fields);

    public Task SuccessAsync(string title, string message, params DialogField[] fields) =>
        ShowAsync(DialogKind.Success, title, message, fields);

    public Task WarningAsync(string title, string message, params DialogField[] fields) =>
        ShowAsync(DialogKind.Warning, title, message, fields);

    public Task ErrorAsync(string title, string message, params DialogField[] fields) =>
        ShowAsync(DialogKind.Error, title, message, fields);

    /// <summary>Single-button dialog with a semantic icon, wrapped message and optional fields.</summary>
    public async Task ShowAsync(DialogKind kind, string title, string message, params DialogField[] fields)
    {
        var content = DialogContent.Build(kind, message, fields);
        var dialog = CreateDialog(title, content);
        // Simple messages use a more compact width
        if (fields.Length == 0)
            dialog.DialogMaxWidth = 480;
        dialog.CloseButtonText = "确定";
        dialog.CloseButtonAppearance = ControlAppearance.Primary;
        dialog.DefaultButton = ContentDialogButton.Close;
        await _contentDialogService.ShowAsync(dialog, default);
    }

    /// <summary>
    /// Confirmation dialog. Returns true only when the user picks the confirm button.
    /// Destructive actions get a danger-styled confirm button and default to Cancel.
    /// </summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText,
        bool destructive = false, params DialogField[] fields)
    {
        var kind = destructive ? DialogKind.Warning : DialogKind.Info;
        var content = DialogContent.Build(kind, message, fields);
        var dialog = CreateDialog(title, content);
        // Confirmations with fields need more width
        if (fields.Length > 0)
            dialog.DialogMaxWidth = 560;
        else
            dialog.DialogMaxWidth = 480;
        dialog.PrimaryButtonText = confirmText;
        dialog.PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary;
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        var result = await _contentDialogService.ShowAsync(dialog, default);
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Confirmation dialog with one extra checkbox (unchecked by default).
    /// Returns null when cancelled, otherwise whether the checkbox was checked.
    /// </summary>
    public async Task<bool?> ConfirmWithOptionAsync(string title, string message, string optionText,
        string confirmText, bool destructive = false)
    {
        var option = new CheckBox { Content = new System.Windows.Controls.TextBlock { Text = optionText, TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 16, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(DialogContent.Build(destructive ? DialogKind.Warning : DialogKind.Info, message, []));
        body.Children.Add(option);

        var dialog = CreateDialog(title, body);
        dialog.DialogMaxWidth = 520;
        dialog.PrimaryButtonText = confirmText;
        dialog.PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary;
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        var result = await _contentDialogService.ShowAsync(dialog, default);
        return result == ContentDialogResult.Primary ? option.IsChecked == true : null;
    }

    /// <summary>
    /// Form dialog. <paramref name="submit"/> runs when the user presses the primary button and
    /// returns null on success or an error message, which is shown inside the dialog so the
    /// user can correct the input without losing it. Returns true once submit succeeded.
    /// </summary>
    public async Task<bool> ShowFormAsync(string title, FrameworkElement form, string primaryText,
        Func<Task<string?>> submit, double maxWidth = 560)
    {
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false,
            Title = "操作失败", Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(form);
        body.Children.Add(error);

        var dialog = CreateDialog(title, body);
        dialog.DialogMaxWidth = maxWidth;
        dialog.PrimaryButtonText = primaryText;
        dialog.PrimaryButtonAppearance = ControlAppearance.Primary;
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var succeeded = false;
        dialog.Closing += async (_, e) =>
        {
            if (e.Result != ContentDialogResult.Primary || succeeded)
                return;

            // Keep the dialog open while submitting; close it ourselves on success.
            e.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            error.IsOpen = false;
            string? message;
            try { message = await submit(); }
            catch (Exception ex) { message = ex.Message; }
            dialog.IsPrimaryButtonEnabled = true;

            if (message == null)
            {
                succeeded = true;
                dialog.Hide(ContentDialogResult.Primary);
            }
            else
            {
                error.Message = message;
                error.IsOpen = true;
            }
        };

        await _contentDialogService.ShowAsync(dialog, default);
        return succeeded;
    }

    private static ContentDialog CreateDialog(string title, object content) => new()
    {
        Title = title,
        Content = content,
        DialogMaxWidth = 560,
        IsFooterVisible = true
    };
}
