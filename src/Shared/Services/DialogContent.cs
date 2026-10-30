using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace RemoteKSP.Shared.Services;

/// <summary>Builds the body of a Fluent dialog: icon + message, then label/value fields.</summary>
internal static class DialogContent
{
    public static FrameworkElement Build(DialogKind kind, string message, DialogField[] fields)
    {
        var root = new StackPanel { MinWidth = 380 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new SymbolIcon { Symbol = SymbolFor(kind), Filled = true, FontSize = 24,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(Control.ForegroundProperty, BrushKeyFor(kind));
        header.Children.Add(icon);

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        root.Children.Add(header);

        if (fields.Length > 0)
        {
            var list = new StackPanel { Margin = new Thickness(36, 16, 0, 0) };
            foreach (var field in fields)
                list.Children.Add(BuildField(field));
            root.Children.Add(list);
        }
        return root;
    }

    private static FrameworkElement BuildField(DialogField field)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var label = new TextBlock { Text = field.Label, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(label);

        // Use TextBlock for short readable text (errors, descriptions)
        // Use TextBox for technical values that need to be copied (thumbprints, IDs, stack traces)
        if (field.Monospace || field.Value.Length > 100)
        {
            // Technical field: use copyable TextBox
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBox = new TextBox
            {
                Text = field.Value,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(8)
            };
            if (field.Monospace)
                textBox.FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New");
            AutomationNameFor(textBox, field.Label);
            row.Children.Add(textBox);

            var copy = new Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
                Appearance = ControlAppearance.Transparent,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = SharedStrings.Dialog_Copy
            };
            AutomationNameFor(copy, string.Format(SharedStrings.Dialog_CopyField, field.Label));
            copy.Click += (_, _) => Clipboard.SetText(field.Value);
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            panel.Children.Add(row);
        }
        else
        {
            // Readable field: use TextBlock with inline copy button
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = field.Value,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Padding = new Thickness(0, 2, 0, 0)
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            row.Children.Add(textBlock);

            var copy = new Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
                Appearance = ControlAppearance.Transparent,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = SharedStrings.Dialog_Copy,
                Width = 28,
                Height = 28
            };
            AutomationNameFor(copy, string.Format(SharedStrings.Dialog_CopyField, field.Label));
            copy.Click += (_, _) => Clipboard.SetText(field.Value);
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            panel.Children.Add(row);
        }

        return panel;
    }

    private static void AutomationNameFor(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static SymbolRegular SymbolFor(DialogKind kind) => kind switch
    {
        DialogKind.Success => SymbolRegular.CheckmarkCircle24,
        DialogKind.Warning => SymbolRegular.Warning24,
        DialogKind.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24
    };

    private static string BrushKeyFor(DialogKind kind) => kind switch
    {
        DialogKind.Success => "SystemFillColorSuccessBrush",
        DialogKind.Warning => "SystemFillColorCautionBrush",
        DialogKind.Error => "SystemFillColorCriticalBrush",
        _ => "AccentTextFillColorPrimaryBrush"
    };
}
