using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace RemoteKSP.Shared.Ui;

/// <summary>
/// 客户端和服务端共用的侧边导航栏布局：汉堡按钮与第一个导航项的间距和导航项之间完全相同，接近 Windows 11 任务管理器。
/// <para>
/// WPF-UI 4.3 的 NavigationView 模板在汉堡按钮和菜单列表之间依次放了：
/// 汉堡按钮下边距 5、AutoSuggestBox 占位的下边距 6（即使没有搜索框）、顶部分隔线（高 1、上下边距各 4），合计约 20px。
/// 这里全部去掉：不显示顶部分隔线，没有搜索框时去掉占位的边距，汉堡按钮下边距设为 0。
/// </para>
/// <para>
/// 导航项之间布局间距为 0，看到的空隙来自各自 1px 的边框（悬停/选中背景画在边框内侧）。
/// 汉堡按钮的模板同样有 1px 边框，所以下边距为 0 时它与第一个导航项的可见空隙正好等于导航项之间的空隙。
/// 汉堡按钮自身的位置、尺寸和点击区域不变。展开和折叠用同一个模板，两种状态一致；底部 Settings 所在的行不受影响。
/// </para>
/// </summary>
public static class NavigationPaneLayout
{
    /// <summary>模板里 AutoSuggestBox 占位元素的名字（WPF-UI 4.3 NavigationViewLeft 模板）</summary>
    private const string AutoSuggestBoxPresenterName = "AutoSuggestBoxContentPresenter";

    /// <summary>模板里汉堡按钮的名字（WPF-UI 4.3）</summary>
    private const string ToggleButtonName = "PART_ToggleButton";

    public static readonly DependencyProperty CompactPaneHeaderProperty = DependencyProperty.RegisterAttached(
        "CompactPaneHeader", typeof(bool), typeof(NavigationPaneLayout),
        new PropertyMetadata(false, OnCompactPaneHeaderChanged));

    public static bool GetCompactPaneHeader(DependencyObject element) => (bool)element.GetValue(CompactPaneHeaderProperty);

    public static void SetCompactPaneHeader(DependencyObject element, bool value) => element.SetValue(CompactPaneHeaderProperty, value);

    private static void OnCompactPaneHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NavigationView nav || e.NewValue is not true)
            return;

        nav.IsTopSeparatorVisible = false;
        nav.Loaded -= OnLoaded;
        nav.Loaded += OnLoaded;
        if (nav.IsLoaded)
            OnLoaded(nav, new RoutedEventArgs());
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var nav = (NavigationView)sender;
        CollapseEmptyAutoSuggestBox(nav);
        RemoveToggleBottomMargin(nav);
    }

    /// <summary>汉堡按钮的下边距（模板里是 0,0,0,5）设为 0；只影响它下方内容的起点，不改按钮本身</summary>
    private static void RemoveToggleBottomMargin(NavigationView nav)
    {
        if (FindDescendant(nav, ToggleButtonName) is { } toggle)
            toggle.Margin = new Thickness(toggle.Margin.Left, toggle.Margin.Top, toggle.Margin.Right, 0);
    }

    /// <summary>
    /// 没有搜索框时，去掉占位元素的下边距：它没有内容，占的 6px 全部来自这个边距。
    /// 不改 Visibility：WPF-UI 每次展开/折叠都会在代码里重设它（展开 Visible、折叠 Hidden），改了也会被覆盖；
    /// 边距只由模板设置，本地值优先级更高，展开/折叠后都保持为 0
    /// </summary>
    private static void CollapseEmptyAutoSuggestBox(NavigationView nav)
    {
        if (nav.AutoSuggestBox != null)
            return;
        if (FindDescendant(nav, AutoSuggestBoxPresenterName) is { } presenter)
            presenter.Margin = new Thickness(0);
    }

    private static FrameworkElement? FindDescendant(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var n } fe && n == name)
                return fe;
            if (FindDescendant(child, name) is { } found)
                return found;
        }
        return null;
    }
}
