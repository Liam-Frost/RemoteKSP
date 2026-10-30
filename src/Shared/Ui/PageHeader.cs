using System.Windows;

namespace RemoteKSP.Shared.Ui;

/// <summary>
/// 页面在窗口标题行（NavigationView.Header，显示「概览」等页面标题）右侧放的操作按钮。
/// 页面只声明按钮，由 MainWindow 在导航后放进标题行，按钮与标题同一行、右边缘与页面内容对齐。
/// 按钮的 DataContext 是页面本身，绑定写 <c>{Binding ViewModel.XxxCommand}</c>。
/// </summary>
public static class PageHeader
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.RegisterAttached(
        "Actions", typeof(FrameworkElement), typeof(PageHeader), new PropertyMetadata(null));

    public static FrameworkElement? GetActions(DependencyObject element) => (FrameworkElement?)element.GetValue(ActionsProperty);

    public static void SetActions(DependencyObject element, FrameworkElement? value) => element.SetValue(ActionsProperty, value);

    /// <summary>MainWindow 导航后调用：取出页面声明的按钮并绑定到页面</summary>
    public static FrameworkElement? TakeActions(object? page)
    {
        if (page is not FrameworkElement fe || GetActions(fe) is not { } actions)
            return null;
        actions.DataContext = fe;
        return actions;
    }
}
