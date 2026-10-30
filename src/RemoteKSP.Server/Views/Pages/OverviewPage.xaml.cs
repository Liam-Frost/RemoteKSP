using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Server.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Server.Views.Pages;

public partial class OverviewPage : INavigableView<OverviewViewModel>
{
    public OverviewViewModel ViewModel { get; }

    public OverviewPage(OverviewViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        Loaded += async (s, e) => await ViewModel.InitializeAsync();
        // 页面是 Transient：离开页面时取消进行中的刷新和 NetworkChange 静态事件订阅，避免页面无法回收
        Unloaded += (s, e) => ViewModel.Deactivate();
    }

    /// <summary>「更多操作」：鼠标、Enter/空格、辅助技术都走 Click，菜单在按钮下方弹出</summary>
    private void OnMoreClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
