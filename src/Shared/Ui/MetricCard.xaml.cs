using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RemoteKSP.Shared.Ui;

/// <summary>
/// 首页状态卡片。图标颜色统一表示功能分类；成功/警告/错误只通过 <see cref="Status"/> 表达，
/// 并且必须来自真实状态，没有依据时保持 <see cref="StatusKind.None"/>
/// </summary>
public partial class MetricCard : UserControl
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(SymbolRegular), typeof(MetricCard), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MetricCard), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(MetricCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(MetricCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DetailToolTipProperty = DependencyProperty.Register(
        nameof(DetailToolTip), typeof(object), typeof(MetricCard), new PropertyMetadata(null));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(StatusKind), typeof(MetricCard),
        new PropertyMetadata(StatusKind.None, (d, _) => ((MetricCard)d).UpdateStatus()));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(MetricCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IsWideProperty = DependencyProperty.Register(
        nameof(IsWide), typeof(bool), typeof(MetricCard),
        new PropertyMetadata(false, (d, _) => ((MetricCard)d).UpdateLayoutMode()));

    public SymbolRegular Symbol { get => (SymbolRegular)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public object? DetailToolTip { get => GetValue(DetailToolTipProperty); set => SetValue(DetailToolTipProperty, value); }
    public StatusKind Status { get => (StatusKind)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    /// <summary>全宽卡片：操作放在右侧；多列卡片：操作放在底部</summary>
    public bool IsWide { get => (bool)GetValue(IsWideProperty); set => SetValue(IsWideProperty, value); }

    public MetricCard()
    {
        InitializeComponent();
        UpdateStatus();
        UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        if (ActionsHost == null)
            return;
        if (IsWide)
        {
            Grid.SetRow(ActionsHost, 0);
            Grid.SetRowSpan(ActionsHost, 3);
            Grid.SetColumn(ActionsHost, 2);
            Grid.SetColumnSpan(ActionsHost, 1);
            ActionsHost.VerticalAlignment = VerticalAlignment.Center;
            ActionsHost.Margin = new Thickness(16, 0, 0, 0);
        }
        else
        {
            Grid.SetRow(ActionsHost, 2);
            Grid.SetRowSpan(ActionsHost, 1);
            Grid.SetColumn(ActionsHost, 1);
            Grid.SetColumnSpan(ActionsHost, 2);
            ActionsHost.VerticalAlignment = VerticalAlignment.Bottom;
            ActionsHost.Margin = new Thickness(0, 12, 0, 0);
        }
    }

    private void UpdateStatus()
    {
        if (StatusMark == null)
            return;
        StatusMark.Visibility = Status == StatusKind.None ? Visibility.Collapsed : Visibility.Visible;
        StatusRing.Visibility = Status == StatusKind.Busy ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = Status == StatusKind.Busy ? Visibility.Collapsed : Visibility.Visible;

        var (symbol, brush) = Status switch
        {
            StatusKind.Success => (SymbolRegular.CheckmarkCircle16, "SystemFillColorSuccessBrush"),
            StatusKind.Caution => (SymbolRegular.Warning16, "SystemFillColorCautionBrush"),
            StatusKind.Critical => (SymbolRegular.ErrorCircle16, "SystemFillColorCriticalBrush"),
            _ => (SymbolRegular.Info16, "TextFillColorSecondaryBrush")
        };
        StatusIcon.Symbol = symbol;
        StatusIcon.SetResourceReference(ForegroundProperty, brush);
    }
}
